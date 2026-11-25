using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ZenTimings.BiosOrigin;

namespace ZenTimings.Cli
{
    internal sealed class LinuxFirmware
    {
        private readonly string root;
        public LinuxFirmware(string sysfsRoot) => root = Path.GetFullPath(sysfsRoot);
        public string EfiDirectory => Path.Combine(root, "firmware", "efi", "efivars");
        public bool IsAvailable => Directory.Exists(EfiDirectory);
        public string Dmi(string name)
        {
            try { return File.ReadAllText(Path.Combine(root, "class", "dmi", "id", name)).Trim(); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        public string Bios => Dmi("bios_version");
        public string Board => DrfxiSettingOrigins.ResolveBoardIdentity(Dmi("board_name"), Bios,
            Dmi("sys_vendor"), Dmi("product_family"), Dmi("product_name"));

        internal sealed record Variable(uint Attributes, byte[] Payload);

        public Variable Read(string name, string guid)
        {
            // File names are catalog-controlled, not arbitrary paths. EFI GUIDs in Linux
            // filenames use canonical lowercase UUIDs without braces.
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_]+\z") || !Guid.TryParse(guid, out var id))
                throw new ArgumentException("Invalid UEFI variable name/GUID.");
            var path = Path.Combine(EfiDirectory, name + "-" + id.ToString("D"));
            try
            {
                // Do not open for write, even to clear efivarfs immutable flags.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Cap capture even for fixture paths or unexpected firmware.
                const int max = 1024 * 1024;
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int count;
                while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
                {
                    if (buffer.Length + count > max) throw new InvalidDataException(name + ": variable exceeds 1 MiB limit.");
                    buffer.Write(chunk, 0, count);
                }
                byte[] bytes = buffer.ToArray();
                if (bytes.Length < 4) throw new InvalidDataException(name + ": truncated efivarfs attribute header.");
                return new Variable(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)), bytes[4..]);
            }
            catch (FileNotFoundException) { return null; }
            // A vanished/missing directory is not equivalent to an optional absent variable.
        }
    }
}
