// JoyPro.WtOodle: GPL-3.0-or-later (see README.md).
using System;
using System.Globalization;
using System.IO;

namespace JoyPro.WtOodle
{
    internal static class Program
    {
        // serve: "JPWO", then requests on stdin, answers on stdout, until stdin closes or a request has length -1.
        //   request:  int32 packedLength, int32 rawLength, int32 stopAfter, packed bytes
        //   answer:   int32 status (0 = ok, 1 = error), int32 length, bytes (the raw data or the UTF-8 error text)
        // decode <file> <offset> <packedLength> <rawLength> <out>: one block from a file (for checks).
        private static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "serve")
                return Serve();
            if (args.Length == 6 && args[0] == "decode")
            {
                byte[] all = File.ReadAllBytes(args[1]);
                int offset = int.Parse(args[2], CultureInfo.InvariantCulture);
                int length = int.Parse(args[3], CultureInfo.InvariantCulture);
                int raw = int.Parse(args[4], CultureInfo.InvariantCulture);
                try
                {
                    byte[] data = OodleLz.Decompress(all, offset, length, raw);
                    File.WriteAllBytes(args[5], data);
                    Console.WriteLine("ok " + data.Length);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("error " + ex.Message);
                    return 1;
                }
            }
            Console.Error.WriteLine("JoyPro.WtOodle serve | decode <file> <offset> <packedLength> <rawLength> <out>");
            return 2;
        }

        private static int Serve()
        {
            using (Stream input = Console.OpenStandardInput())
            using (Stream output = Console.OpenStandardOutput())
            {
                BinaryReader r = new BinaryReader(input);
                BinaryWriter w = new BinaryWriter(output);
                // The client starts with "JPWO" (anything before it, such as a text encoding's preamble, is skipped).
                int seen = 0;
                while (seen < 4)
                {
                    int b = input.ReadByte();
                    if (b < 0)
                        return 0;
                    seen = b == "JPWO"[seen] ? seen + 1 : (b == 'J' ? 1 : 0);
                }
                for (;;)
                {
                    int packed;
                    try
                    {
                        packed = r.ReadInt32();
                    }
                    catch (EndOfStreamException)
                    {
                        return 0;
                    }
                    if (packed < 0)
                        return 0;
                    int raw = r.ReadInt32();
                    int stop = r.ReadInt32();
                    byte[] data = r.ReadBytes(packed);
                    byte[] answer;
                    int status = 0;
                    try
                    {
                        if (data.Length != packed)
                            throw new InvalidOperationException("short request");
                        answer = OodleLz.Decompress(data, 0, data.Length, raw, stop);
                    }
                    catch (Exception ex)
                    {
                        status = 1;
                        answer = System.Text.Encoding.UTF8.GetBytes(ex.Message);
                    }
                    w.Write(status);
                    w.Write(answer.Length);
                    w.Write(answer);
                    w.Flush();
                }
            }
        }
    }
}
