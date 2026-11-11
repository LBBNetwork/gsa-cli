// SetLargeAddressAware.cs - build-time utility (see D54 in DECISIONS.md).
//
// Sets IMAGE_FILE_LARGE_ADDRESS_AWARE (bit 0x200) in a PE image's
// Characteristics field so that, when this image is loaded as the process
// image on a 32-bit Windows host with PAE enabled, the process gets 4 GB of
// user-mode address space instead of ~2 GB. On 64-bit hosts and on POSIX
// systems the bit is ignored - no downside either way.
//
// Only the EXE (the process image) needs this; DLLs loaded into an already-
// running process inherit its address space, so build scripts patch
// gsa-cli.exe only.
//
// Idempotent: if the bit is already set, reports it and exits 0.
// Verifies by re-reading the file from disk after writing.
using System;
using System.IO;

public class SetLargeAddressAware
{
    private const ushort LargeAddressAwareBit = 0x0200;

    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: SetLA <pe-image>");
            return 1;
        }

        string path = args[0];
        byte[] image;
        try
        {
            image = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: cannot read " + path + ": " + ex.Message);
            return 2;
        }

        int offset = CharacteristicsOffset(image);
        if (offset < 0)
        {
            Console.Error.WriteLine("error: " + path + " is not a recognizable PE image");
            return 3;
        }

        ushort before = ReadUInt16(image, offset);
        if ((before & LargeAddressAwareBit) != 0)
        {
            Console.WriteLine(path + ": LARGEADDRESSAWARE already set (Characteristics 0x" + before.ToString("X4") + ")");
            return 0;
        }

        ushort after = (ushort)(before | LargeAddressAwareBit);
        image[offset] = (byte)(after & 0xFF);
        image[offset + 1] = (byte)((after >> 8) & 0xFF);

        try
        {
            File.WriteAllBytes(path, image);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: cannot write " + path + ": " + ex.Message);
            return 4;
        }

        // Verify from disk, not from the in-memory copy.
        byte[] check = File.ReadAllBytes(path);
        ushort verified = ReadUInt16(check, offset);
        if ((verified & LargeAddressAwareBit) == 0)
        {
            Console.Error.WriteLine("error: verification failed - bit not set after write");
            return 5;
        }

        Console.WriteLine(path + ": LARGEADDRESSAWARE set (Characteristics 0x" + before.ToString("X4") + " -> 0x" + verified.ToString("X4") + ")");
        return 0;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    // Returns the file offset of the COFF Characteristics field, or -1 if the
    // buffer is not a recognizable PE image.
    private static int CharacteristicsOffset(byte[] pe)
    {
        if (pe.Length < 0x40)
        {
            return -1;
        }

        if (pe[0] != (byte)'M' || pe[1] != (byte)'Z')
        {
            return -1; // not a DOS header
        }

        int peOffset = pe[0x3C] | (pe[0x3D] << 8) | (pe[0x3E] << 16) | (pe[0x3F] << 24);
        if (peOffset < 0 || peOffset + 24 > pe.Length)
        {
            return -1; // e_lfanew out of range
        }

        if (pe[peOffset] != (byte)'P' || pe[peOffset + 1] != (byte)'E' || pe[peOffset + 2] != 0 || pe[peOffset + 3] != 0)
        {
            return -1; // not a PE signature
        }

        return peOffset + 0x16; // COFF header: Characteristics is the uint16 at offset +20
    }
}
