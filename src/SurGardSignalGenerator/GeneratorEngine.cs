using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace SurGardSignalGenerator;

public sealed record GeneratorOptions(
    string Host,
    decimal SignalsPerSecond,
    TimeSpan Duration,
    int AckTimeoutMilliseconds,
    int MaximumConcurrentSignals,
    bool RandomOrder,
    IReadOnlyList<string> Objects,
    string ReportPath);

public sealed record SignalResult(
    DateTimeOffset Timestamp,
    string ObjectNumber,
    int Port,
    string Status,
    double ElapsedMilliseconds,
    string Detail,
    string FrameHex);

public sealed record GeneratorSnapshot(
    long Scheduled,
    long Acknowledged,
    long Failed,
    int InFlight,
    double AverageAckMilliseconds,
    double MaximumAckMilliseconds,
    double ElapsedSeconds,
    bool IsRunning,
    string? ReportPath);

public sealed class GeneratorEngine
{
    private long _scheduled;
    private long _acknowledged;
    private long _failed;
    private long _latencyMicroseconds;
    private long _maximumLatencyMicroseconds;
    private int _inFlight;
    private volatile bool _isRunning;
    private Stopwatch? _stopwatch;
    private string? _reportPath;

    public ConcurrentQueue<SignalResult> RecentResults { get; } = new();

    public GeneratorSnapshot Snapshot()
    {
        var acknowledged = Interlocked.Read(ref _acknowledged);
        return new GeneratorSnapshot(
            Interlocked.Read(ref _scheduled),
            acknowledged,
            Interlocked.Read(ref _failed),
            Volatile.Read(ref _inFlight),
            acknowledged == 0
                ? 0
                : Interlocked.Read(ref _latencyMicroseconds) / 1000.0 / acknowledged,
            Interlocked.Read(ref _maximumLatencyMicroseconds) / 1000.0,
            _stopwatch?.Elapsed.TotalSeconds ?? 0,
            _isRunning,
            _reportPath);
    }

    public async Task RunAsync(GeneratorOptions options, CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Generatorul ruleaza deja.");
        }

        if (options.Objects.Count == 0)
        {
            throw new ArgumentException("Nu exista obiecte selectate.", nameof(options));
        }

        Reset(options.ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);

        await using var report = new StreamWriter(
            new FileStream(options.ReportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await report.WriteLineAsync(
            "Timestamp;Object;Port;Status;ElapsedMs;Detail;FrameHex");

        using var reportGate = new SemaphoreSlim(1, 1);
        using var concurrency = new SemaphoreSlim(
            options.MaximumConcurrentSignals,
            options.MaximumConcurrentSignals);

        _isRunning = true;
        _stopwatch = Stopwatch.StartNew();
        var intervalTicks = Stopwatch.Frequency / (double)options.SignalsPerSecond;
        long sequence = 0;
        var random = new Random();
        var objectSequences = options.Objects.ToDictionary(
            objectNumber => objectNumber,
            _ => 0L,
            StringComparer.Ordinal);

        try
        {
            while (_stopwatch.Elapsed < options.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dueTicks = (long)(sequence * intervalTicks);
                var remainingTicks = dueTicks - _stopwatch.ElapsedTicks;
                if (remainingTicks > Stopwatch.Frequency / 500)
                {
                    var delay = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                await concurrency.WaitAsync(cancellationToken);
                var objectIndex = options.RandomOrder
                    ? random.Next(options.Objects.Count)
                    : (int)(sequence % options.Objects.Count);
                var objectNumber = options.Objects[objectIndex];
                var objectSequence = objectSequences[objectNumber];
                objectSequences[objectNumber] = objectSequence + 1;
                sequence++;
                Interlocked.Increment(ref _scheduled);
                Interlocked.Increment(ref _inFlight);

                _ = SendAndRecordAsync(
                    options,
                    objectNumber,
                    objectSequence,
                    report,
                    reportGate,
                    cancellationToken).ContinueWith(
                    _ =>
                    {
                        Interlocked.Decrement(ref _inFlight);
                        concurrency.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            for (var index = 0; index < options.MaximumConcurrentSignals; index++)
            {
                await concurrency.WaitAsync(CancellationToken.None);
            }

            for (var index = 0; index < options.MaximumConcurrentSignals; index++)
            {
                concurrency.Release();
            }

            await report.FlushAsync(CancellationToken.None);
            _stopwatch.Stop();
            _isRunning = false;
        }
    }

    private async Task SendAndRecordAsync(
        GeneratorOptions options,
        string objectNumber,
        long objectSequence,
        StreamWriter report,
        SemaphoreSlim reportGate,
        CancellationToken runCancellationToken)
    {
        var frame = PimaProtocol.BuildFrame(objectNumber, objectSequence);
        var port = PimaProtocol.PortForObject(objectNumber);
        var timer = Stopwatch.StartNew();
        var status = "ERROR";
        var detail = "";

        try
        {
            using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken);
            operationTimeout.CancelAfter(options.AckTimeoutMilliseconds);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(options.Host, port, operationTimeout.Token);
            using var stream = client.GetStream();
            await stream.WriteAsync(frame, operationTimeout.Token);

            var response = new byte[1];
            var read = await stream.ReadAsync(response, operationTimeout.Token);
            if (read == 1 && response[0] == 0x06)
            {
                status = "ACK";
                detail = "06";
                var latency = (long)(timer.Elapsed.TotalMilliseconds * 1000);
                Interlocked.Increment(ref _acknowledged);
                Interlocked.Add(ref _latencyMicroseconds, latency);
                UpdateMaximumLatency(latency);
            }
            else
            {
                detail = read == 0
                    ? "Conexiune inchisa fara ACK"
                    : $"Raspuns 0x{response[0]:X2}, asteptat 0x06";
                Interlocked.Increment(ref _failed);
            }
        }
        catch (OperationCanceledException)
        {
            status = runCancellationToken.IsCancellationRequested ? "STOP" : "TIMEOUT";
            detail = runCancellationToken.IsCancellationRequested
                ? "Oprit de utilizator"
                : $"Fara ACK in {options.AckTimeoutMilliseconds} ms";
            Interlocked.Increment(ref _failed);
        }
        catch (Exception exception)
        {
            detail = exception.Message.Replace(';', ',');
            Interlocked.Increment(ref _failed);
        }
        finally
        {
            timer.Stop();
        }

        var result = new SignalResult(
            DateTimeOffset.Now,
            objectNumber,
            port,
            status,
            timer.Elapsed.TotalMilliseconds,
            detail,
            Convert.ToHexString(frame));
        RecentResults.Enqueue(result);

        await reportGate.WaitAsync(CancellationToken.None);
        try
        {
            await report.WriteLineAsync(string.Join(';',
                result.Timestamp.ToString("O"),
                result.ObjectNumber,
                result.Port,
                result.Status,
                result.ElapsedMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                result.Detail,
                result.FrameHex));
        }
        finally
        {
            reportGate.Release();
        }
    }

    private void Reset(string reportPath)
    {
        Interlocked.Exchange(ref _scheduled, 0);
        Interlocked.Exchange(ref _acknowledged, 0);
        Interlocked.Exchange(ref _failed, 0);
        Interlocked.Exchange(ref _latencyMicroseconds, 0);
        Interlocked.Exchange(ref _maximumLatencyMicroseconds, 0);
        Interlocked.Exchange(ref _inFlight, 0);
        while (RecentResults.TryDequeue(out _))
        {
        }

        _reportPath = reportPath;
    }

    private void UpdateMaximumLatency(long latencyMicroseconds)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _maximumLatencyMicroseconds);
            if (latencyMicroseconds <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _maximumLatencyMicroseconds,
                    latencyMicroseconds,
                    current) == current)
            {
                return;
            }
        }
    }
}
