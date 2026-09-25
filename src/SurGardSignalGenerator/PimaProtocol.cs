using System.Globalization;
using System.Text;

namespace SurGardSignalGenerator;

public static class PimaProtocol
{
    public const int FrameLength = 38;

    // Synthetic identifiers used by default. Replace them only in an authorized lab.
    public static readonly IReadOnlyList<string> DemoObjects =
    [
        "10001", "10002", "10003", "10004",
        "20001", "20002", "20003", "20004",
        "30001", "30002", "30003", "30004"
    ];

    public static int PortForObject(string objectNumber)
    {
        ValidateObject(objectNumber);
        return 11000 + (objectNumber[0] - '0');
    }

    public static string RawObjectFor(string objectNumber)
    {
        ValidateObject(objectNumber);
        return "00" + objectNumber[1..];
    }

    public static byte[] BuildFrame(string objectNumber, long objectSequence)
    {
        if (objectSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectSequence));
        }

        var port = PortForObject(objectNumber);
        var receiverHeader = port == 11001 ? "234" : "214";
        var eventCodes = port == 11002
            ? new[] { 401, 402, 400, 602, 608 }
            : new[] { 402, 401, 400, 602, 608 };

        var zone = objectSequence % 1000;
        var partition = ((objectSequence / 1000) % 100 + 1) % 100;
        var eventCode = eventCodes[(int)((objectSequence / 100_000) % eventCodes.Length)];
        var qualifier = (objectSequence / 500_000) % 2 == 0 ? 1 : 3;
        var contactId = $"{qualifier}{eventCode:000}{partition:00}{zone:000}";
        var eventField = receiverHeader.PadRight(19, '_');
        var payloadWithoutChecksum = eventField + RawObjectFor(objectNumber) + contactId;
        var checksum = CalculateChecksum(payloadWithoutChecksum);
        var frame = "\n" + payloadWithoutChecksum + checksum + "\r";

        var bytes = Encoding.ASCII.GetBytes(frame);
        if (bytes.Length != FrameLength)
        {
            throw new InvalidOperationException($"Cadrul generat are {bytes.Length} bytes, nu {FrameLength}.");
        }

        return bytes;
    }

    public static byte[] BuildRepeatedB2Frame(string objectNumber)
    {
        var port = PortForObject(objectNumber);
        var eventField = port == 11001
            ? "59304______________"
            : "59303______________";
        return BuildRawFrame(eventField + RawObjectFor(objectNumber) + "B2_______");
    }

    private static byte[] BuildRawFrame(string payloadWithoutChecksum)
    {
        var checksum = CalculateChecksum(payloadWithoutChecksum);
        var frame = "\n" + payloadWithoutChecksum + checksum + "\r";
        var bytes = Encoding.ASCII.GetBytes(frame);
        if (bytes.Length != FrameLength)
        {
            throw new InvalidOperationException($"Cadrul generat are {bytes.Length} bytes, nu {FrameLength}.");
        }

        return bytes;
    }

    public static string CalculateChecksum(string payloadWithoutChecksum)
    {
        var sum = 0;
        foreach (var character in payloadWithoutChecksum)
        {
            if (character == '_')
            {
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                throw new ArgumentException(
                    $"Caracter neacceptat pentru checksum: '{character}'.",
                    nameof(payloadWithoutChecksum));
            }

            sum += int.Parse(character.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return ((0xFF - (sum & 0xFF)) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static void ValidateObject(string objectNumber)
    {
        if (objectNumber.Length != 5
            || objectNumber[0] is < '1' or > '3'
            || objectNumber.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "Numarul Andromeda trebuie sa aiba 5 cifre si sa inceapa cu 1, 2 sau 3.",
                nameof(objectNumber));
        }
    }
}
