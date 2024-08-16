using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crank.EventSources;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Tracing;

namespace HttpClientBenchmarks
{
    class Program
    {
        private static readonly double s_msPerTick = 1000.0 / Stopwatch.Frequency;

        private const int c_SingleThreadRpsEstimate = 3000;

        private static ClientOptions s_options = null!;
        private static Uri s_url = null!;
        private static List<HttpMessageInvoker> s_httpClients = new List<HttpMessageInvoker>();
        private static Metrics s_metrics = new Metrics();

        private static List<(string Name, string? Value)> s_generatedStaticHeaders = new List<(string Name, string? Value)>();
        private static List<string> s_generatedDynamicHeaderNames = new List<string>();

        private static byte[]? s_requestContentData = null;
        private static byte[]? s_requestContentLastChunk = null;
        private static int s_fullChunkCount;
        private static bool s_useByteArrayContent;

        private static bool s_isWarmup;
        private static bool s_isRunning;

        private static IHttpClientFactory? s_httpClientFactory;
        private static IHttpMessageHandlerFactory? s_httpMessageHandlerFactory;
        private static string[]? s_httpClientNames;

        private static Func<int, HttpMessageInvoker>? s_clientFactory;
        private static EventListener? s_listener;

        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand();
            ClientOptionsBinder.AddOptionsToCommand(rootCommand);

            rootCommand.SetHandler(async (ClientOptions options) =>
                {
                    s_options = options;
                    Log("HttpClient benchmark");
                    Log("Options: " + s_options);
                    ValidateOptions();

                    await Setup().ConfigureAwait(false);
                    Log("Setup done");

                    await RunScenario().ConfigureAwait(false);
                    Log("Scenario done");
                },
                new ClientOptionsBinder());

            return await rootCommand.InvokeAsync(args).ConfigureAwait(false);
        }

        private static void ValidateOptions()
        {
#if NET6_0_OR_GREATER
            if (!s_options.UseHttps && s_options.HttpVersion == HttpVersion.Version30)
            {
                throw new ArgumentException("HTTP/3.0 only supports HTTPS");
            }
#endif

            if (s_options.UseWinHttpHandler && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new ArgumentException("WinHttpHandler is only supported on Windows");
            }

            if (s_options.Scenario == "get" && s_options.ContentSize != 0)
            {
                throw new ArgumentException("Expected to have ContentSize = 0 for 'get' scenario, got " + s_options.ContentSize);
            }

            if (s_options.Scenario == "post" && s_options.ContentSize <= 0)
            {
                throw new ArgumentException("Expected to have ContentSize > 0 for 'post' scenario, got " + s_options.ContentSize);
            }

            if (s_options.UseHttpMessageInvoker && s_options.UseDefaultRequestHeaders)
            {
                throw new ArgumentException("HttpMessageInvoker does not support DefaultRequestHeaders");
            }
        }

        private static async Task Setup()
        {
            BenchmarksEventSource.Register("env/processorcount", Operations.First, Operations.First, "Processor Count", "Processor Count", "n0");
            LogMetric("env/processorcount", Environment.ProcessorCount);

            var baseUrl = $"http{(s_options.UseHttps ? "s" : "")}://{s_options.Address}:{s_options.Port}";
            s_url = new Uri(baseUrl + s_options.Path);
            Log("Base url: " + baseUrl);
            Log("Full url: " + s_url);

            if (s_options.GeneratedStaticHeadersCount > 0 || s_options.GeneratedDynamicHeadersCount > 0)
            {
                CreateGeneratedHeaders();
            }

            s_clientFactory = s_options.UseHttpClientFactory
                ? CreateDIHttpClientFactory()
                : CreateStaticHttpClientFactory();

            if (s_options.ContentSize > 0)
            {
                CreateRequestContentData();
            }

            // First request to the server; to ensure everything started correctly
            var request = CreateRequest(HttpMethod.Get, new Uri(baseUrl));
            var stopwatch = Stopwatch.StartNew();
            var response = await SendAsync(s_clientFactory(0), request).ConfigureAwait(false);
            var elapsed = stopwatch.ElapsedMilliseconds;
            response.EnsureSuccessStatusCode();
            BenchmarksEventSource.Register("http/firstrequest", Operations.Max, Operations.Max, "First request duration (ms)", "Duration of the first request to the server (ms)", "n0");
            LogMetric("http/firstrequest", elapsed);
        }

        private static Func<int, HttpMessageInvoker> CreateDIHttpClientFactory()
        {
            s_httpClientNames = new string[s_options.NumberOfHttpClients];
            for (int i = 0; i < s_options.NumberOfHttpClients; ++i)
            {
                s_httpClientNames[i] = "client_" + i;
            }

            var services = new ServiceCollection();
            for (int i = 0; i < s_options.NumberOfHttpClients; ++i)
            {
                var config = services.AddHttpClient(s_httpClientNames[i])
                    .ConfigurePrimaryHttpMessageHandler(() => CreateHandler())
                    .SetHandlerLifetime(TimeSpan.FromSeconds(s_options.HcfHandlerLifetime));

                if (s_options.UseDefaultRequestHeaders)
                {
                    config.ConfigureHttpClient(client =>
                    {
                        AddStaticHeaders(client.DefaultRequestHeaders);
                    });
                }
            }
            var serviceProvider = services.BuildServiceProvider();

            if (s_options.UseHttpMessageInvoker)
            {
                s_httpMessageHandlerFactory = serviceProvider.GetRequiredService<IHttpMessageHandlerFactory>();
                return static i => new HttpMessageInvoker(s_httpMessageHandlerFactory.CreateHandler(s_httpClientNames[i]));
            }
            else
            {
                s_httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                return static i => s_httpClientFactory.CreateClient(s_httpClientNames[i]);
            }
        }

        private static Func<int, HttpMessageInvoker> CreateStaticHttpClientFactory()
        {
            for (int i = 0; i < s_options.NumberOfHttpClients; ++i)
            {
                if (s_options.UseHttpMessageInvoker)
                {
                    s_httpClients.Add(new HttpMessageInvoker(CreateHandler()));
                }
                else
                {
                    var client = new HttpClient(CreateHandler());
                    if (s_options.UseDefaultRequestHeaders)
                    {
                        AddStaticHeaders(client.DefaultRequestHeaders);
                    }
                    s_httpClients.Add(client);
                }
            }

            return static i => s_httpClients[i];
        }

        private static HttpMessageHandler CreateHandler()
        {
            int max11ConnectionsPerServer = s_options.Http11MaxConnectionsPerServer > 0 ? s_options.Http11MaxConnectionsPerServer : int.MaxValue;
            TimeSpan pooledConnectionLifetime = s_options.PooledConnectionLifetime >= 0 ? TimeSpan.FromSeconds(s_options.PooledConnectionLifetime) : Timeout.InfiniteTimeSpan;

            if (s_options.UseWinHttpHandler)
            {
                // Disable "only supported on: 'windows'" warning -- options are already validated
#pragma warning disable CA1416
                return new WinHttpHandler()
                {
                    // accept all certs
                    ServerCertificateValidationCallback = delegate { return true; },
                    MaxConnectionsPerServer = max11ConnectionsPerServer,
                    EnableMultipleHttp2Connections = s_options.Http20EnableMultipleConnections
                };
#pragma warning restore CA1416
            }
            else
            {
                return new SocketsHttpHandler()
                {
                    // accept all certs
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } },
                    MaxConnectionsPerServer = max11ConnectionsPerServer,
                    EnableMultipleHttp2Connections = s_options.Http20EnableMultipleConnections,
                    ConnectTimeout = Timeout.InfiniteTimeSpan,
                    PooledConnectionLifetime = pooledConnectionLifetime
                };
            }
        }

        private static async Task RunScenario()
        {
            BenchmarksEventSource.Register("http/requests", Operations.Sum, Operations.Sum, "Requests", "Number of requests", "n0");
            BenchmarksEventSource.Register("http/requests/badresponses", Operations.Sum, Operations.Sum, "Bad Status Code Requests", "Number of requests with bad status codes", "n0");
            BenchmarksEventSource.Register("http/requests/errors", Operations.Sum, Operations.Sum, "Exceptions", "Number of exceptions", "n0");
            BenchmarksEventSource.Register("http/rps/mean", Operations.Avg, Operations.Avg, "Mean RPS", "Requests per second - mean", "n0");

            if (s_options.CollectRequestTimings)
            {
                RegisterPercentiledMetric("http/headers", "Time to response headers (ms)", "Time to response headers (ms)");
                RegisterPercentiledMetric("http/contentstart", "Time to first response content byte (ms)", "Time to first response content byte (ms)");
                RegisterPercentiledMetric("http/contentend", "Time to last response content byte (ms)", "Time to last response content byte (ms)");
            }

            Func<Func<HttpMessageInvoker>, bool, Task<Metrics>> scenario;
            switch(s_options.Scenario)
            {
                case "get":
                    scenario = Get;
                    break;
                case "post":
                    scenario = Post;
                    break;
                default:
                    throw new ArgumentException($"Unknown scenario: {s_options.Scenario}");
            }

            s_isWarmup = true;
            s_isRunning = true;

            var coordinatorTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(s_options.Warmup)).ConfigureAwait(false);
                s_isWarmup = false;
                StartCountersListener();
                Log("Completing warmup...");

                await Task.Delay(TimeSpan.FromSeconds(s_options.Duration)).ConfigureAwait(false);
                s_isRunning = false;
                Log("Completing scenario...");
            });

            var tasks = new List<Task<Metrics>>(s_options.NumberOfHttpClients * s_options.ConcurrencyPerHttpClient);
            for (int i = 0; i < s_options.NumberOfHttpClients; ++i)
            {
                var idx = i;
                var ithClientFactory = () => s_clientFactory!(idx);
                for (int j = 0; j < s_options.ConcurrencyPerHttpClient; ++j)
                {
                    tasks.Add(scenario(/* clientFactory: */ ithClientFactory, /* shortLivedClient: */ s_options.UseHttpClientFactory));
                }
            }

            await coordinatorTask.ConfigureAwait(false);
            var metricsArray = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var metrics in metricsArray)
            {
                s_metrics.Add(metrics);
            }

            LogMetric("http/requests", s_metrics.SuccessRequests + s_metrics.BadStatusRequests);
            LogMetric("http/requests/badresponses", s_metrics.BadStatusRequests);
            LogMetric("http/requests/errors", s_metrics.ExceptionRequests);
            LogMetric("http/rps/mean", s_metrics.MeanRps);

            if (s_options.CollectRequestTimings && s_metrics.SuccessRequests > 0)
            {
                LogPercentiledMetric("http/headers", s_metrics.HeadersTimes, TicksToMs);
                LogPercentiledMetric("http/contentstart", s_metrics.ContentStartTimes, TicksToMs);
                LogPercentiledMetric("http/contentend", s_metrics.ContentEndTimes, TicksToMs);
            }
        }

        private static void StartCountersListener()
        {
            if (s_options.HttpVersion == HttpVersion.Version30)
            {
                s_listener = new CountersListener(NetEventCounters.EventCounters_H3);
            }
            else if (s_options.HttpVersion == HttpVersion.Version20)
            {
                s_listener = new CountersListener(NetEventCounters.EventCounters_H2);
            }
            else
            {
                s_listener = new CountersListener(NetEventCounters.EventCounters_H1);
            }
        }

        private static Task<Metrics> Get(Func<HttpMessageInvoker> clientFactory, bool shortLivedClient)
        {
            if (shortLivedClient)
            {
                return Measure(async () =>
                {
                    using var client = clientFactory(); // created each time
                    return await SendGetRequestAsync(client).ConfigureAwait(false);
                });
            }

            var client = clientFactory(); // created once
            return Measure(() => SendGetRequestAsync(client));

            static Task<HttpResponseMessage> SendGetRequestAsync(HttpMessageInvoker client)
            {
                var request = CreateRequest(HttpMethod.Get, s_url);
                return SendAsync(client, request);
            }
        }

        private static Task<Metrics> Post(Func<HttpMessageInvoker> clientFactory, bool shortLivedClient)
        {
            if (shortLivedClient)
            {
                return Measure(async () =>
                {
                    using var client = clientFactory(); // created each time
                    return await SendPostRequestAsync(client).ConfigureAwait(false);
                });
            }

            var client = clientFactory(); // created once
            return Measure(() => SendPostRequestAsync(client));

            static async Task<HttpResponseMessage> SendPostRequestAsync(HttpMessageInvoker client)
            {
                var request = CreateRequest(HttpMethod.Post, s_url);

                Task<HttpResponseMessage> responseTask;
                if (s_useByteArrayContent)
                {
                    request.Content = new ByteArrayContent(s_requestContentData!);
                    responseTask = SendAsync(client, request);
                }
                else
                {
                    var requestContent = new StreamingHttpContent();
                    request.Content = requestContent;

                    if (!s_options.ContentUnknownLength)
                    {
                        request.Content.Headers.ContentLength = s_options.ContentSize;
                    }
                    // Otherwise, we don't need to set TransferEncodingChunked for HTTP/1.1 manually, it's done automatically if ContentLength is absent

                    responseTask = SendAsync(client, request);
                    var requestContentStream = await requestContent.GetStreamAsync().ConfigureAwait(false);

                    for (int i = 0; i < s_fullChunkCount; ++i)
                    {
                        await requestContentStream.WriteAsync(s_requestContentData).ConfigureAwait(false);
                        if (s_options.ContentFlushAfterWrite)
                        {
                            await requestContentStream.FlushAsync().ConfigureAwait(false);
                        }
                    }
                    if (s_requestContentLastChunk != null)
                    {
                        await requestContentStream.WriteAsync(s_requestContentLastChunk).ConfigureAwait(false);
                    }
                    requestContent.CompleteStream();
                }

                return await responseTask.ConfigureAwait(false);
            }
        }

        private static async Task<Metrics> Measure(Func<Task<HttpResponseMessage>> sendAsync)
        {
            var drainArray = new byte[ClientOptions.DefaultBufferSize];
            var stopwatch = Stopwatch.StartNew();

            var metrics = s_options.CollectRequestTimings
                ? new Metrics(s_options.Duration * c_SingleThreadRpsEstimate)
                : new Metrics();
            bool isWarmup = true;

            var durationStopwatch = Stopwatch.StartNew();
            while (s_isRunning)
            {
                if (isWarmup && !s_isWarmup)
                {
                    if (metrics.SuccessRequests == 0)
                    {
                        throw new Exception($"No successful requests during warmup.");
                    }
                    isWarmup = false;
                    metrics.SuccessRequests = 0;
                    metrics.BadStatusRequests = 0;
                    metrics.ExceptionRequests = 0;
                    durationStopwatch.Restart();
                }

                try
                {
                    stopwatch.Restart();
                    using HttpResponseMessage result = await sendAsync().ConfigureAwait(false);
                    var headersTime = stopwatch.ElapsedTicks;

                    if (result.IsSuccessStatusCode)
                    {
                        using var content = await result.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        var bytesRead = await content.ReadAsync(drainArray).ConfigureAwait(false);
                        var contentStartTime = stopwatch.ElapsedTicks;

                        while (bytesRead != 0)
                        {
                            bytesRead = await content.ReadAsync(drainArray).ConfigureAwait(false);
                        }
                        var contentEndTime = stopwatch.ElapsedTicks;

                        if (s_options.CollectRequestTimings && !isWarmup)
                        {
                            metrics.HeadersTimes.Add(headersTime);
                            metrics.ContentStartTimes.Add(contentStartTime);
                            metrics.ContentEndTimes.Add(contentEndTime);
                        }
                        metrics.SuccessRequests++;
                    }
                    else
                    {
                        Log("Bad status code: " + result.StatusCode);
                        metrics.BadStatusRequests++;
                    }
                }
                catch (Exception ex)
                {
                    Log("Exception: " + ex);
                    metrics.ExceptionRequests++;
                }
            }
            var elapsed = durationStopwatch.ElapsedTicks * 1.0 / Stopwatch.Frequency;
            metrics.MeanRps = (metrics.SuccessRequests +  metrics.BadStatusRequests) / elapsed;
            return metrics;
        }

        private static void CreateGeneratedHeaders()
        {
            for (int i = 0; i < s_options.GeneratedStaticHeadersCount; ++i)
            {
                s_generatedStaticHeaders.Add(("X-Generated-Static-Header-" + i, "X-Generated-Value-" + i));
            }

            for (int i = 0; i < s_options.GeneratedDynamicHeadersCount; ++i)
            {
                s_generatedDynamicHeaderNames.Add("X-Generated-Dynamic-Header-" + i);
            }
        }

        private static void CreateRequestContentData()
        {
            s_useByteArrayContent = !s_options.ContentUnknownLength && (!s_options.ContentFlushAfterWrite || s_options.ContentWriteSize >= s_options.ContentSize); // no streaming
            if (s_useByteArrayContent)
            {
                s_fullChunkCount = 1;
                s_requestContentData = new byte[s_options.ContentSize];
                Array.Fill(s_requestContentData, (byte)'a');
            }
            else
            {
                s_fullChunkCount = s_options.ContentSize / s_options.ContentWriteSize;
                if (s_fullChunkCount != 0)
                {
                    s_requestContentData = new byte[s_options.ContentWriteSize];
                    Array.Fill(s_requestContentData, (byte)'a');
                }
                int lastChunkSize = s_options.ContentSize % s_options.ContentWriteSize;
                if (lastChunkSize != 0)
                {
                    s_requestContentLastChunk = new byte[lastChunkSize];
                    Array.Fill(s_requestContentLastChunk, (byte)'a');
                }
            }
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri) =>
            new HttpRequestMessage(method, uri) {
                Version = s_options.HttpVersion!,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

        private static Task<HttpResponseMessage> SendAsync(HttpMessageInvoker client, HttpRequestMessage request)
        {
            if (!s_options.UseDefaultRequestHeaders)
            {
                AddStaticHeaders(request.Headers);
            }

            if (s_options.GeneratedDynamicHeadersCount > 0)
            {
                AddDynamicHeaders(request.Headers);
            }

            return s_options.UseHttpMessageInvoker
                ? client.SendAsync(request, CancellationToken.None)
                : ((HttpClient)client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        private static void AddStaticHeaders(HttpRequestHeaders headers)
        {
            foreach (var header in s_options.Headers)
            {
                AddHeader(headers, header.Name, header.Value);
            }

            for (int i = 0; i < s_options.GeneratedStaticHeadersCount; ++i)
            {
                AddHeader(headers, s_generatedStaticHeaders[i].Name, s_generatedStaticHeaders[i].Value);
            }
        }

        private static void AddDynamicHeaders(HttpRequestHeaders headers)
        {
            var value = headers.GetHashCode().ToString();
            for (int i = 0; i < s_options.GeneratedDynamicHeadersCount; ++i)
            {
                AddHeader(headers, s_generatedDynamicHeaderNames[i], value);
            }
        }

        private static void AddHeader(HttpRequestHeaders headers, string name, string? value)
        {
            if (!headers.TryAddWithoutValidation(name, value))
            {
                throw new Exception($"Unable to add header \"{name}: {value}\" to the request");
            }
        }

        public static void Log(string message)
        {
            var time = DateTime.UtcNow.ToString("hh:mm:ss.fff");
            Console.WriteLine($"[{time}] {message}");
        }

        private static double TicksToMs(double ticks) => ticks * s_msPerTick;

        private static double GetPercentile(int percent, List<long> sortedValues)
        {
            if (percent == 0)
            {
                return sortedValues[0];
            }

            if (percent == 100)
            {
                return sortedValues[sortedValues.Count - 1];
            }

            var i = ((long)percent * sortedValues.Count) / 100.0 + 0.5;
            var fractionPart = i - Math.Truncate(i);

            return (1.0 - fractionPart) * sortedValues[(int)Math.Truncate(i) - 1] + fractionPart * sortedValues[(int)Math.Ceiling(i) - 1];
        }

        private static void RegisterPercentiledMetric(string name, string shortDescription, string longDescription)
        {
            BenchmarksEventSource.Register(name + "/min", Operations.Min, Operations.Min, shortDescription + " - min", longDescription + " - min", "n2");
            BenchmarksEventSource.Register(name + "/p50", Operations.Max, Operations.Max, shortDescription + " - p50", longDescription + " - 50th percentile", "n2");
            BenchmarksEventSource.Register(name + "/p75", Operations.Max, Operations.Max, shortDescription + " - p75", longDescription + " - 75th percentile", "n2");
            BenchmarksEventSource.Register(name + "/p90", Operations.Max, Operations.Max, shortDescription + " - p90", longDescription + " - 90th percentile", "n2");
            BenchmarksEventSource.Register(name + "/p99", Operations.Max, Operations.Max, shortDescription + " - p99", longDescription + " - 99th percentile", "n2");
            BenchmarksEventSource.Register(name + "/max", Operations.Max, Operations.Max, shortDescription + " - max", longDescription + " - max", "n2");
        }

        private static void LogPercentiledMetric(string name, List<long> values, Func<double, double> prepareValue)
        {
            values.Sort();

            LogMetric(name + "/min", prepareValue(GetPercentile(0, values)));
            LogMetric(name + "/p50", prepareValue(GetPercentile(50, values)));
            LogMetric(name + "/p75", prepareValue(GetPercentile(75, values)));
            LogMetric(name + "/p90", prepareValue(GetPercentile(90, values)));
            LogMetric(name + "/p99", prepareValue(GetPercentile(99, values)));
            LogMetric(name + "/max", prepareValue(GetPercentile(100, values)));
        }

        public static void LogMetric(string name, double value)
        {
            BenchmarksEventSource.Measure(name, value);
            Log($"{name}: {value}");
        }

        private static void LogMetric(string name, long value)
        {
            BenchmarksEventSource.Measure(name, value);
            Log($"{name}: {value}");
        }
    }

    public static class NetEventCounters
    {
        /*private static readonly List<string> s_aspNetCoreHttpConnections = new List<string>
        {
            "connections-started",
            "connections-stopped",
            "connections-timed-out",
            "current-connections",
            "connections-duration",
        };

        private static readonly List<string> s_aspNetCoreKestrel = new List<string>
        {
            "connections-per-second",
            "total-connections",
            "tls-handshakes-per-second",
            "total-tls-handshakes",
            "current-tls-handshakes",
            "failed-tls-handshakes",
            "current-connections",
            "connection-queue-length",
            "request-queue-length",
            "current-upgraded-requests",
        };*/

        private static readonly List<string> s_systemNetHttp_H1 = new List<string>
        {
            "http11-connections-current-total",
            "http11-requests-queue-duration",
        };

        private static readonly List<string> s_systemNetHttp_H2 = new List<string>
        {
            "http20-connections-current-total",
            "http20-requests-queue-duration",
        };

        private static readonly List<string> s_systemNetHttp_H3 = new List<string>
        {
            "http30-connections-current-total",
            "http30-requests-queue-duration",
        };

        private static readonly List<string> s_systemNetSecurity = new List<string>
        {
            "tls-handshake-rate",
            "total-tls-handshakes",
            "current-tls-handshakes",
            "failed-tls-handshakes",
            "tls13-sessions-open",
            "tls13-handshake-duration",
        };

        private static readonly List<string> s_systemNetSockets = new List<string>
        {
            "current-outgoing-connect-attempts",
            "outgoing-connections-established",
        };

        public static Dictionary<string, List<string>> EventCounters_H1 { get; } = new Dictionary<string, List<string>>
        {
            //{ "Microsoft.AspNetCore.Http.Connections",  s_aspNetCoreHttpConnections },
            //{ "Microsoft-AspNetCore-Server-Kestrel",    s_aspNetCoreKestrel },
            //{ "System.Net.Http",                        s_systemNetHttp },
            { "System.Net.Http",                        s_systemNetHttp_H1 },
            { "System.Net.Security",                    s_systemNetSecurity },
            { "System.Net.Sockets",                     s_systemNetSockets },
        };

        public static Dictionary<string, List<string>> EventCounters_H2 { get; } = new Dictionary<string, List<string>>
        {
            { "System.Net.Http",                        s_systemNetHttp_H2 },
            { "System.Net.Security",                    s_systemNetSecurity },
            { "System.Net.Sockets",                     s_systemNetSockets },
        };

        public static Dictionary<string, List<string>> EventCounters_H3 { get; } = new Dictionary<string, List<string>>
        {
            { "System.Net.Http",                        s_systemNetHttp_H3 },
        };
    }

    public sealed class CountersListener : EventListener
    {
        private List<EventSource> _eventSources = new List<EventSource>();
        private Dictionary<string, List<CounterKey>> _eventCounters { get; } = null!;

        record struct CounterKey(string EventSourceName, string CounterName, string? Type = null);
        record struct CounterMetric(string MetricName, string DisplayName, Operations operation = Operations.First, bool IsTotal = false);
        private static Dictionary<CounterKey, CounterMetric> CounterMetricData = new Dictionary<CounterKey, CounterMetric>();

        public static void Log(string message) => Program.Log(message);
        public static void LogMetric(string name, double value) => BenchmarksEventSource.Measure(name, value);

        public CountersListener(Dictionary<string, List<string>> eventCounters)
        {
            List<EventSource> eventSources = _eventSources;

            lock (this)
            {
                _eventCounters = eventCounters.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Select(counterName => new CounterKey(kvp.Key, counterName)).ToList());
                _eventSources = null!;
            }

            // eventSources were populated in the base ctor and are now owned by this thread, enable them now.
            foreach (EventSource eventSource in eventSources)
            {
                if (_eventCounters.ContainsKey(eventSource.Name))
                {
                    EnableEventSource(eventSource);
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            // We're likely called from base ctor, if so, just save the event source for later initialization.
            if (_eventCounters is null)
            {
                lock (this)
                {
                    if (_eventCounters is null)
                    {
                        _eventSources.Add(eventSource);
                        return;
                    }
                }
            }

            // Second pass called after our ctor, allow logging for specified source names.
            if (_eventCounters.ContainsKey(eventSource.Name))
            {
                EnableEventSource(eventSource);
            }
        }

        private void EnableEventSource(EventSource eventSource)
        {
            var eventLevel = EventLevel.Critical;
            var arguments = new Dictionary<string, string?> { { "EventCounterIntervalSec", "1" } };

            EnableEvents(eventSource, eventLevel, EventKeywords.None, arguments);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var eventSourceName = eventData.EventSource.Name;
            if (eventData.EventId == -1)
            {
                if (!_eventCounters.TryGetValue(eventSourceName, out var allowedCounters))
                {
                    return;
                }

                if (eventData.EventName != "EventCounters" ||
                    eventData.Payload?.Count != 1 ||
                    eventData.Payload[0] is not IDictionary<string, object> counters ||
                    !counters.TryGetValue("Name", out var obj) ||
                    obj is not string name)
                {
                    Log($"Failed to parse EventCounters event from {eventSourceName}");
                    return;
                }

                var baseCounterKey = new CounterKey(eventSourceName, name);
                if (!allowedCounters.Contains(baseCounterKey))
                {
                    return;
                }

                if (!CounterMetricData.TryGetValue(baseCounterKey, out var baseCounterMetric))
                {
                    if (!counters.TryGetValue("DisplayName", out obj) || obj is not string baseDisplayName)
                    {
                        Log($"Failed to parse {name} event from {eventSourceName}");
                        return;
                    }
                    if (counters.TryGetValue("DisplayUnits", out obj) && obj is string units && units.Length > 0)
                    {
                        baseDisplayName += $" ({units})";
                    }
                    var baseName = $"{baseCounterKey.EventSourceName.Replace(".", "-")}/{baseCounterKey.CounterName}";

                    baseCounterMetric = new CounterMetric(baseName, baseDisplayName, IsTotal: baseDisplayName.StartsWith("Total"));
                    CounterMetricData[baseCounterKey] = baseCounterMetric;
                }

                bool logged = Measure(baseCounterKey, baseCounterMetric, counters, "Increment", skipTypeInDisplayName: true);

                if (!logged)
                {
                    if (baseCounterMetric.IsTotal)
                    {
                        logged |= Measure(baseCounterKey, baseCounterMetric, counters, "Max", skipTypeInDisplayName: true);
                    }
                    else
                    {
                        logged |= Measure(baseCounterKey, baseCounterMetric, counters, "Mean");
                        logged |= Measure(baseCounterKey, baseCounterMetric, counters, "Max");
                    }

                }

                if (!logged)
                {
                    Log($"Failed to parse {name} event value from {eventSourceName}");
                }
            }
            else if (eventData.EventId == 0)
            {
                Log($"Received an error message from EventSource {eventSourceName}: {eventData.Message}");
            }
            else
            {
                Log($"Received an unknown event from EventSource {eventSourceName}: {eventData.EventName} ({eventData.EventId})");
            }

            static bool Measure(CounterKey baseCounterKey, CounterMetric baseCounterMetric, IDictionary<string, object> eventData, string type, bool skipTypeInDisplayName = false)
            {
                if (!eventData.TryGetValue(type, out var obj) || obj is not double value)
                {
                    return false;
                }

                var counterKey = baseCounterKey with { Type = type };
                if (!CounterMetricData.TryGetValue(counterKey, out var counterMetric))
                {
                    var operation = type switch
                    {
                        "Mean" => Operations.Avg,
                        "Increment" => Operations.Sum,
                        "Max" => Operations.Max,
                        _ => Operations.First
                    };

                    var displayName = baseCounterMetric.DisplayName;
                    if (!skipTypeInDisplayName)
                    {
                        displayName += $" - {type}";
                    }

                    counterMetric = new CounterMetric(
                        $"{baseCounterMetric.MetricName}/{operation}".ToLowerInvariant(),
                        displayName,
                        operation);

                    CounterMetricData[counterKey] = counterMetric;

                    BenchmarksEventSource.Register(
                        counterMetric.MetricName,
                        operation,
                        Operations.Sum,
                        counterMetric.DisplayName,
                        counterMetric.DisplayName,
                        "n0");
                }

                LogMetric(counterMetric.MetricName, value);
                return true;
            }
        }
    }
}
