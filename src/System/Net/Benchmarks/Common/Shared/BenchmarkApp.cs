﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace System.Net.Benchmarks;

public abstract class BenchmarkApp<TOptions> where TOptions : new()
{
    private static bool s_appStarted;

    protected static CancellationTokenSource GlobalCts { get; } = new();

    protected abstract string Name { get; }
    protected abstract string MetricPrefix { get; }

    protected static void Log(string message) => LogHelper.Log(message);

    protected void LogMetric(string name, string description, double value, string format = "n2")
    {
        var metricName = MetricPrefix + name;
        LogHelper.RegisterSimpleMetric(metricName, description, format);
        LogHelper.LogMetric(metricName, value);
    }

    protected void LogMetricPercentiles(string name, string description, List<double> values, string format = "n3")
    {
        var metricName = MetricPrefix + name;
        LogHelper.RegisterPercentileMetric(metricName, description, description, format);
        LogHelper.LogPercentileMetric(metricName, values);
    }

    protected virtual void ValidateOptions(TOptions options) { }
    protected abstract Task RunBenchmarkAsync(TOptions options, CancellationToken cancellationToken);

    public Task<int> RunAsync<TBinder>(string[] args)
        where TBinder : BenchmarkOptionsBinder<TOptions>, new()
    {
        if (s_appStarted)
        {
            throw new InvalidOperationException($"{Name} is already running.");
        }
        s_appStarted = true;

        var rootCommand = new RootCommand(Name);
        var binder = new TBinder();
        binder.AddCommandLineArguments(rootCommand);
        rootCommand.SetHandler(RunAsyncInternal, binder);

        return rootCommand.InvokeAsync(args);
    }

    private async Task<int> RunAsyncInternal(TOptions options)
    {
        Log($"Starting {Name}");
        Log($"Options:");
        Log($"{options}");

        ValidateOptions(options);

        Console.CancelKeyPress += static (s, e) =>
        {
            Log("Keyboard interrupt...");
            e.Cancel = true;
            GlobalCts.Cancel();
        };

        // NOTE:
        // It is better for metrics to be registered with some delay to ensure the metadata is collected.
        // Event listener is started (shortly) after the benchmark app starts, so it might miss the registration event.
        _ = Task.Run(static async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), GlobalCts.Token).ConfigureAwait(false);
            LogHelper.RegisterSimpleMetric("env/processorcount", "Processor Count", "n0");
            LogHelper.LogMetric("env/processorcount", Environment.ProcessorCount);
        });

        try
        {
            await RunBenchmarkAsync(options, GlobalCts.Token);
        }
        catch (Exception e)
        {
            Log("Unhandled exception: " + e);
            throw;
        }
        return 0; // on success, returning 0 explicitly; otherwise RootCommand (?) will not report unhandled exceptions to crank as failures
    }
}
