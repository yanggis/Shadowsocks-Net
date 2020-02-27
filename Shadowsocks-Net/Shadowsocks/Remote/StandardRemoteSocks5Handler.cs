﻿/*
 * Shadowsocks-Net https://github.com/shadowsocks/Shadowsocks-Net
 */

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Argument.Check;


namespace Shadowsocks.Remote
{
    using Infrastructure;
    using Infrastructure.Sockets;
    using Infrastructure.Pipe;


#pragma warning disable CA1063 // Implement IDisposable Correctly
    public class StandardRemoteSocks5Handler : ISocks5Handler
#pragma warning restore CA1063 // Implement IDisposable Correctly
    {
        ILogger _logger = null;

        RemoteServerConfig _remoteServerConfig = null;
        DnsCache _dnsCache = null;

        List<DefaultPipe> _pipes = null;
        object _pipesReadWriteLock = new object();

        public StandardRemoteSocks5Handler(RemoteServerConfig remoteServerConfig, DnsCache dnsCache, ILogger logger = null)
        {
            _remoteServerConfig = Throw.IfNull(() => remoteServerConfig);
            _dnsCache = Throw.IfNull(() => dnsCache);
            _logger = logger;

            _pipes = new List<DefaultPipe>();
        }
        ~StandardRemoteSocks5Handler()
        {
            Cleanup();
        }


        public async Task HandleTcp(IClient client, CancellationToken cancellationToken = default)
        {
            if (null == client) { return; }
            using (SmartBuffer localRequestCipher = SmartBuffer.Rent(Defaults.ReceiveBufferSize))
            {
                localRequestCipher.SignificantLength = await client.ReadAsync(localRequestCipher.Memory, cancellationToken);

                //decrypt
                var cipher = _remoteServerConfig.CreateCipher(_logger);
                using (var localReqestPlain = cipher.DecryptTcp(localRequestCipher.SignificanMemory))
                {
                    if (0 == localRequestCipher.SignificantLength)
                    {//decrypt failed, available options: 1.leave it. 2.close connection. 3.add to blocklist.

                        _logger?.LogWarning($"StandardRemoteSocks5Handler HandleTcp decrypt failed, client=[{client.EndPoint.ToString()}]");
                        client.Close();//->local pipe broken-> local pipe close.
                        return;
                    }
                    IPAddress targetIP = IPAddress.Any; //TODO target address check
                    if (ShadowsocksAddress.TryResolve(localReqestPlain.Memory, out ShadowsocksAddress ssaddr))//resolve target address
                    {
                        if (0x3 == ssaddr.ATYP)//a domain name
                        {
                            var ips = await _dnsCache.ResolveHost(Encoding.UTF8.GetString(ssaddr.Address.ToArray()));
                            if (ips != null && ips.Length > 0) { targetIP = ips[0]; }
                        }
                        else
                        {
                            targetIP = new IPAddress(ssaddr.Address.Span);
                        }
                        if (IPAddress.Any != targetIP)//got target IP
                        {
                            IPEndPoint ipeTarget = new IPEndPoint(targetIP, ssaddr.Port);
                            _logger.LogInformation($"connecting to [{ipeTarget.ToString()}]...");
                            var targetClient = await TcpClient1.ConnectAsync(ipeTarget, _logger);//connect target

                            if (null != targetClient)
                            {
                                _logger.LogInformation($"connected to [{ipeTarget.ToString()}]");
                                int written = await targetClient.WriteAsync(localReqestPlain.Memory.Slice(ssaddr.RawMemory.Length, 
                                    localReqestPlain.SignificantLength - ssaddr.RawMemory.Length), cancellationToken);
                                _logger.LogInformation($"wrote {written} bytes to [{ipeTarget.ToString()}]");
                                _logger.LogInformation($"payload={Encoding.UTF8.GetString(localReqestPlain.Memory.Slice(ssaddr.RawMemory.Length, localReqestPlain.SignificantLength - ssaddr.RawMemory.Length).ToArray())}");
                                if (written > 0)
                                {
                                    await PipeTcp(client, targetClient, cipher, cancellationToken);
                                    return;
                                }
                            }
                            else
                            {
                                _logger?.LogInformation($"StandardRemoteSocks5Handler HandleTcp unable to connect target [{ipeTarget.ToString()}]. client=[{client.EndPoint.ToString()}]");
                                client.Close();
                                return;
                            }
                        }
                        else//resolve target address failed.
                        {
                            _logger?.LogWarning($"StandardRemoteSocks5Handler HandleTcp invalid target addr. client=[{client.EndPoint.ToString()}]");
                            client.Close();
                            return;
                        }
                    }
                    else//invalid socks5 addr
                    {
                        _logger?.LogWarning($"StandardRemoteSocks5Handler HandleTcp resolve target addr failed. client=[{client.EndPoint.ToString()}]");
                        client.Close();
                        return;
                    }
                }
            }
            await Task.CompletedTask;
        }

        public async Task HandleUdp(IClient client, CancellationToken cancellationToken = default)
        {
            if (null == client) { return; }
            using (SmartBuffer localRequestCipher = SmartBuffer.Rent(1500))
            {
                localRequestCipher.SignificantLength = await client.ReadAsync(localRequestCipher.Memory, cancellationToken);
                //decrypt
                var cipher = _remoteServerConfig.CreateCipher(_logger);
                using (var localReqestPlain = cipher.DecryptUdp(localRequestCipher.SignificanMemory))
                {
                    if (0 == localRequestCipher.SignificantLength)
                    {//decrypt failed, available options: 1.leave it. 2.close connection. 3.add to blocklist.

                        _logger?.LogWarning($"StandardRemoteSocks5Handler HandleUdp decrypt failed, client=[{client.EndPoint.ToString()}]");
                        client.Close();//->local pipe broken-> local pipe close.
                        return;
                    }
                    IPAddress targetIP = IPAddress.Any; //TODO target address check
                    if (ShadowsocksAddress.TryResolve(localReqestPlain.Memory, out ShadowsocksAddress ssaddr))//resolve target address
                    {
                        if (0x3 == ssaddr.ATYP)//a domain name
                        {
                            var ips = await _dnsCache.ResolveHost(Encoding.UTF8.GetString(ssaddr.Address.ToArray()));
                            if (ips != null && ips.Length > 0) { targetIP = ips[0]; }
                        }
                        else
                        {
                            targetIP = new IPAddress(ssaddr.Address.Span);
                        }
                        if (IPAddress.Any != targetIP)//got target IP
                        {
                            IPEndPoint ipeTarget = new IPEndPoint(targetIP, ssaddr.Port);
                            var targetClient = await UdpClient1.ConnectAsync(ipeTarget, _logger);
                            if (null != targetClient)
                            {
                                await targetClient.WriteAsync(localReqestPlain.Memory.Slice(ssaddr.RawMemory.Length), cancellationToken);

                                await PipeUdp(client, targetClient, cipher, cancellationToken);
                            }
                            else
                            {
                                _logger?.LogInformation($"StandardRemoteSocks5Handler HandleUdp unable to connect target [{ipeTarget.ToString()}]. client=[{client.EndPoint.ToString()}]");
                                client.Close();
                                return;
                            }
                        }
                        else//resolve target address failed.
                        {
                            _logger?.LogWarning($"StandardRemoteSocks5Handler HandleUdp invalid target addr. client=[{client.EndPoint.ToString()}]");
                            client.Close();
                            return;
                        }

                    }
                    else
                    {
                        _logger?.LogWarning($"StandardRemoteSocks5Handler HandleUdp resolve target addr failed. client=[{client.EndPoint.ToString()}]");
                        client.Close();
                        return;
                    }
                }
            }


            await Task.CompletedTask;
        }


        async Task PipeTcp(IClient client, IClient relayClient, Cipher.IShadowsocksStreamCipher cipher, CancellationToken cancellationToken, params PipeFilter[] addFilters)
        {
            DefaultPipe p = new DefaultPipe(relayClient, client, Defaults.ReceiveBufferSize, _logger);
            p.OnBroken += Pipe_OnBroken;

            Cipher.CipherTcpFilter filter1 = new Cipher.CipherTcpFilter(client, cipher, _logger);
            
            p.ApplyFilter(filter1);
            
            //if (addFilters.Length > 0)
            //{
            //    foreach (var f in addFilters)
            //    {
            //        p.ApplyFilter(f);
            //    }
            //}
            lock (_pipesReadWriteLock)
            {
                this._pipes.Add(p);
            }
            p.Pipe();
            await Task.CompletedTask;
        }

        async Task PipeUdp(IClient client, IClient relayClient, Cipher.IShadowsocksStreamCipher cipher, CancellationToken cancellationToken, params PipeFilter[] addFilters)
        {
            DefaultPipe p = new DefaultPipe(relayClient, client, 1500, _logger);
            p.OnBroken += Pipe_OnBroken;

            Cipher.CipherUdpFilter filter1 = new Cipher.CipherUdpFilter(client, cipher, _logger);
            RemoteUdpRelayPackingFilter filter2 = new RemoteUdpRelayPackingFilter(relayClient, _logger);
            p.ApplyFilter(filter1)
                .ApplyFilter(filter2);
            //if (addFilters.Length > 0)
            //{
            //    foreach (var f in addFilters)
            //    {
            //        p.ApplyFilter(f);
            //    }
            //}
            lock (_pipesReadWriteLock)
            {
                this._pipes.Add(p);
            }

            p.Pipe();
            await Task.CompletedTask;
        }


        private void Pipe_OnBroken(object sender, PipeEventArgs e)
        {
            var p = e.Pipe as DefaultPipe;
            p.OnBroken -= this.Pipe_OnBroken;
            p.UnPipe();
            p.ClientA.Close();
            p.ClientB.Close();

            lock (_pipesReadWriteLock)
            {
                this._pipes.Remove(p);
            }
        }

        void Cleanup()
        {
            foreach (var p in this._pipes)
            {
                p.UnPipe();
                p.ClientA.Close();
                p.ClientB.Close();
            }
            lock (_pipesReadWriteLock)
            {
                this._pipes.Clear();
            }

        }
        public void Dispose()
        {
            Cleanup();
        }
    }
}
