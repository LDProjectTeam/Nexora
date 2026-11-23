using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vellum.Core.Helpers
{
    public static class HashHelper
    {
        /// <summary>Compute SHA-256 of a file and return lowercase hex string.</summary>
        public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[81920];
            int read;
            while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            var sb = new StringBuilder(64);
            foreach (byte b in sha.Hash!)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Compare two hash strings case-insensitively.</summary>
        public static bool HashesMatch(string a, string b)
            => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
