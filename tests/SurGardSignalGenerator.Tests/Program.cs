using System.Text;
using System.Net;
using System.Net.Sockets;
using SurGardSignalGenerator;

var failures = new List<string>();

Check("Lista are 12 obiecte demo", PimaProtocol.DemoObjects.Count == 12);
Check("Obiectele sunt unice", PimaProtocol.DemoObjects.Distinct().Count() == 12);
Check("Port 10001", PimaProtocol.PortForObject("10001") == 11001);
Check("Port 20001", PimaProtocol.PortForObject("20001") == 11002);
Check("Port 30001", PimaProtocol.PortForObject("30001") == 11003);
Check("Raw 10001", PimaProtocol.RawObjectFor("10001") == "000001");
Check("Raw 20001", PimaProtocol.RawObjectFor("20001") == "000001");
Check("Raw 30001", PimaProtocol.RawObjectFor("30001") == "000001");

Check("Checksum minim", PimaProtocol.CalculateChecksum("0") == "FF");
Check("Checksum hexazecimal", PimaProtocol.CalculateChecksum("123ABC") == "D8");

Check(
    "Cadru B2 demonstrativ 20001",
    Encoding.ASCII.GetString(PimaProtocol.BuildRepeatedB2Frame("20001")) ==
    "\n59303______________000001B2_______DD\r");

Check(
    "Cadru CID demonstrativ 10001",
    Encoding.ASCII.GetString(PimaProtocol.BuildFrame("10001", 40)).Length == PimaProtocol.FrameLength);

Check(
    "Cadre CID distincte",
    !PimaProtocol.BuildFrame("10001", 0).SequenceEqual(PimaProtocol.BuildFrame("10001", 1)));

foreach (var objectNumber in PimaProtocol.DemoObjects)
{
    var frame = PimaProtocol.BuildFrame(objectNumber, 0);
    Check($"Lungime {objectNumber}", frame.Length == 38);
    Check($"Delimitatori {objectNumber}", frame[0] == 0x0A && frame[^1] == 0x0D);
}

await RunLocalIntegrationTest();

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Toate testele SurGardSignalGenerator au trecut.");
return 0;

void Check(string name, bool condition)
{
    if (!condition)
    {
        failures.Add("FAIL: " + name);
    }
}

async Task RunLocalIntegrationTest()
{
    using var serverCancellation = new CancellationTokenSource();
    var listener = new TcpListener(IPAddress.Loopback, 11001);
    listener.Start();
    var received = 0;
    var uniqueFrames = new HashSet<string>(StringComparer.Ordinal);
    var serverTask = Task.Run(async () =>
    {
        try
        {
            while (!serverCancellation.Token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(serverCancellation.Token);
                using var stream = client.GetStream();
                var frame = new byte[PimaProtocol.FrameLength];
                var offset = 0;
                while (offset < frame.Length)
                {
                    var read = await stream.ReadAsync(frame.AsMemory(offset), serverCancellation.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }

                if (offset == PimaProtocol.FrameLength)
                {
                    Interlocked.Increment(ref received);
                    uniqueFrames.Add(Convert.ToHexString(frame));
                    await stream.WriteAsync(new byte[] { 0x06 }, serverCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    var reportDirectory = Path.Combine(Path.GetTempPath(), "SurGardSignalGenerator.Tests");
    Directory.CreateDirectory(reportDirectory);
    var reportPath = Path.Combine(reportDirectory, $"test-{Guid.NewGuid():N}.csv");
    var engine = new GeneratorEngine();
    await engine.RunAsync(
        new GeneratorOptions(
            "127.0.0.1",
            20,
            TimeSpan.FromSeconds(1),
            1000,
            32,
            false,
            ["10001"],
            reportPath),
        CancellationToken.None);

    var snapshot = engine.Snapshot();
    Check("Integrare: rata aproximativa", snapshot.Scheduled is >= 18 and <= 22);
    Check("Integrare: toate cadrele primite", received == snapshot.Scheduled);
    Check("Integrare: toate cadrele sunt distincte", uniqueFrames.Count == snapshot.Scheduled);
    Check("Integrare: toate ACK", snapshot.Acknowledged == snapshot.Scheduled);
    Check("Integrare: fara erori", snapshot.Failed == 0);
    Check("Integrare: raport CSV", File.Exists(reportPath) && File.ReadLines(reportPath).Count() == snapshot.Scheduled + 1);

    serverCancellation.Cancel();
    listener.Stop();
    await serverTask;
    File.Delete(reportPath);
}
