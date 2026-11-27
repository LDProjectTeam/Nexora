using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vellum.Core.Services
{
    /// <summary>
    /// One-time launch confirmation sent by the bootstrapper over the child process'
    /// inherited standard-input pipe. The token is never placed in the command line or
    /// an environment variable; a manual environment marker or argument is therefore
    /// not enough to bypass the bootstrapper.
    /// </summary>
    public static class BootstrapperHandshake
    {
        private const string Prefix = "VELLUM_BOOTSTRAP_V1:";
        private const int NonceHexLength = 64;

        public static string CreateToken()
        {
            var nonce = RandomNumberGenerator.GetBytes(32);
            return Prefix + Convert.ToHexString(nonce).ToLowerInvariant();
        }

        public static bool IsValidToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            if (!token.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            if (token.Length != Prefix.Length + NonceHexLength)
                return false;

            for (var i = Prefix.Length; i < token.Length; i++)
            {
                var c = token[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the bootstrapper token from inherited standard input. This method is
        /// only used for the very first launch decision; it never blocks a normal
        /// Explorer launch (stdin is not redirected there).
        /// </summary>
        public static string? TryReadFromStandardInput(TimeSpan timeout)
        {
            try
            {
                if (!Console.IsInputRedirected)
                    return null;

                var read = Task.Run(() => Console.In.ReadLine());
                if (!read.Wait(timeout))
                    return null;
                return read.Result;
            }
            catch
            {
                return null;
            }
        }
    }
}
