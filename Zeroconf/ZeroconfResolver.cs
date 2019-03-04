﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Heijden.DNS;
using Type = Heijden.DNS.Type;

namespace Zeroconf
{
    /// <summary>
    ///     Looks for ZeroConf devices
    /// </summary>
    public static partial class ZeroconfResolver
    {
        static readonly AsyncLock ResolverLock = new AsyncLock();

        static readonly INetworkInterface NetworkInterface = new NetworkInterface();

        static IEnumerable<string> BrowseResponseParser(Response response)
        {
            return response.RecordsPTR.Select(ptr => ptr.PTRDNAME);
        }

        static async Task<IDictionary<string, Response>> ResolveInternal(ZeroconfOptions options,
                                                                         Action<string, Response> callback,
                                                                         CancellationToken cancellationToken)
        {
            var requestBytes = GetRequestBytes(options);
            using (options.AllowOverlappedQueries ? Disposable.Empty : await ResolverLock.LockAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dict = new Dictionary<string, Response>();

                void Converter(IPAddress address, byte[] buffer)
                {
                    var resp = new Response(buffer);
                    var firstPtr = resp.RecordsPTR.FirstOrDefault();
                    var name = firstPtr?.PTRDNAME.Split('.')[0] ?? string.Empty;
                    var addrString = address.ToString();

                    Debug.WriteLine($"IP: {addrString}, {(string.IsNullOrEmpty(name) ? string.Empty : $"Name: {name}, ")}Bytes: {buffer.Length}, IsResponse: {resp.header.QR}");

                    if (resp.header.QR)
                    {
                        // see if the IP is the same as the A to filter out dups that might echo from other interfaces


                        var aRec = resp.RecordsA.FirstOrDefault();
                        var additionalARec = resp.Additionals.FirstOrDefault(arr => arr.Type == Type.A)?.RECORD as RecordA;

                        // if we have an aRec or additionalARec, check those
                        bool? matches = null;
                        if (aRec != null || additionalARec != null)
                        {
                            matches = string.Equals(aRec?.Address, addrString, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(additionalARec?.Address, addrString, StringComparison.OrdinalIgnoreCase);
                        }
                        
                        if (matches ?? true)
                        {
                            // Key is IP Address. Multiple responses from the same IP mean truncated DNS response packets.
                            // Reassemble them here...this does break the header and raw fields but downstream
                            // just uses the lists of records.
                            var key = $"{addrString}";// {(string.IsNullOrEmpty(name) ? "" : $": {name}")}";
                            lock (dict)
                            {

                                Response prevresp = null;
                                if (!dict.TryGetValue(key, out prevresp))
                                {
                                    dict[key] = resp;
                                }
                                else
                                {
                                    // Add the new response records to the previous
                                    prevresp.Answers.AddRange(resp.Answers);
                                }
                            }

                            callback?.Invoke(key, resp);
                        }
                    }
                }

                Debug.WriteLine($"Looking for {string.Join(", ", options.Protocols)} with scantime {options.ScanTime}");

                // If there isn't a forced adapter, check all devices
                if (options.Adapter == null)
                {
                    await NetworkInterface.NetworkRequestAsync(requestBytes,
                                                               options.ScanTime,
                                                               options.Retries,
                                                               (int)options.RetryDelay.TotalMilliseconds,
                                                               Converter,
                                                               cancellationToken)
                                          .ConfigureAwait(false);
                }
                else
                {
                    await NetworkInterface.NetworkRequestAsync(requestBytes,
                                           options.ScanTime,
                                           options.Retries,
                                           (int)options.RetryDelay.TotalMilliseconds,
                                           Converter,
                                           options.Adapter,
                                           cancellationToken)
                      .ConfigureAwait(false);
                }

                return dict;
            }
        }

        static byte[] GetRequestBytes(ZeroconfOptions options)
        {
            var req = new Request();
            var queryType = options.ScanQueryType == ScanQueryType.Ptr ? QType.PTR : QType.ANY;

            foreach (var protocol in options.Protocols)
            {
                var question = new Question(protocol, queryType, QClass.ANY);

                req.AddQuestion(question);
            }

            return req.Data;
        }

        static ZeroconfHost ResponseToZeroconf(Response response, string remoteAddress)
        {
            var z = new ZeroconfHost
            {
                IPAddresses = response.Answers
                                      .Select(r => r.RECORD)
                                      .OfType<RecordA>()
                                      .Concat(response.Additionals
                                                      .Select(r => r.RECORD)
                                                      .OfType<RecordA>())
                                      .Select(aRecord => aRecord.Address)
                                      .Distinct()
                                      .ToList()
            };

            z.Id = z.IPAddresses.FirstOrDefault() ?? remoteAddress;

            // Set the host display name from the A record
            z.DisplayName = response.RecordsA.Length > 0 ? 
                response.RecordsA.First().RR.NAME : remoteAddress;
            
            foreach (var ptrRec in response.RecordsPTR)
            {
                // Get the matching service records
                var responseRecords = response.RecordsRR
                                             .Where(r => r.NAME == ptrRec.PTRDNAME)
                                             .Select(r => r.RECORD)
                                             .ToList();

                var srvRec = responseRecords.OfType<RecordSRV>().FirstOrDefault();
                if (srvRec == null)
                    continue; // Missing the SRV record, not valid

                var svc = new Service
                {
                    Name = ptrRec.PTRDNAME,
                    Port = srvRec.PORT,
                    Ttl = (int)srvRec.RR.TTL,
                    Protocol = ptrRec.RR.NAME,
                };

                // There may be 0 or more text records - property sets
                foreach (var txtRec in responseRecords.OfType<RecordTXT>())
                {
                    var set = new Dictionary<string, string>();
                    foreach (var txt in txtRec.TXT)
                    {
                        var split = txt.Split(new[] {'='}, 2);
                        if (split.Length == 1)
                        {
                            if (!string.IsNullOrWhiteSpace(split[0]))
                                set[split[0]] = null;
                        }
                        else
                        {
                            set[split[0]] = split[1];
                        }
                    }
                    svc.AddPropertySet(set);
                }

                z.AddService(svc);
            }

            return z;
        }


    }
}
