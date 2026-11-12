using System.Reflection.PortableExecutable;

internal static class ReadyToRunArtifact
{
    internal static void Verify(string path)
    {
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        var native = pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory
            ?? throw new InvalidDataException("The published application is not a managed PE assembly");
        if (!pe.HasMetadata || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || native.Size < 16 || native.RelativeVirtualAddress == 0)
            throw new InvalidDataException("The published application does not contain a Windows x64 ReadyToRun header");
        var header = pe.GetSectionData(native.RelativeVirtualAddress).GetReader(0, native.Size);
        if (header.ReadUInt32() != 0x00525452) // ReadyToRun signature: RTR\0.
            throw new InvalidDataException("The managed native header is not ReadyToRun");
        ushort major = header.ReadUInt16();
        ushort minor = header.ReadUInt16();
        Console.WriteLine($"Published assembly: Windows x64 ReadyToRun {major}.{minor}, native header {native.Size} bytes verified.");
    }
}
