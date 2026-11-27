using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vellum.ReleaseFeedTool;

internal static class Program
{
    private const int SchemaVersion = 1;
    private const string AppId = "vellum-launcher";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var options = ParseOptions(args.Skip(1).ToArray());
            return args[0].ToLowerInvariant() switch
            {
                "keygen" => KeyGen(options),
                "build" => Build(options),
                "verify" => Verify(options),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int KeyGen(Dictionary<string, string> options)
    {
        var privatePath = Required(options, "private");
        var publicPath = Required(options, "public");
        var keyId = Required(options, "key-id");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicPath))!);

        using var rsa = RSA.Create(3072);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        File.WriteAllText(privatePath, privatePem, new UTF8Encoding(false));
        File.WriteAllText(publicPath,
            $"# key-id: {keyId}{Environment.NewLine}{publicPem}", new UTF8Encoding(false));

        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey));

        Console.WriteLine($"PRIVATE_KEY={Path.GetFullPath(privatePath)}");
        Console.WriteLine($"PUBLIC_KEY={Path.GetFullPath(publicPath)}");
        Console.WriteLine($"KEY_ID={keyId}");
        Console.WriteLine($"PUBLIC_KEY_SHA256={fingerprint}");
        Console.WriteLine("Back up the private key. It must never be committed or uploaded.");
        return 0;
    }

    private static int Build(Dictionary<string, string> options)
    {
        var version = Required(options, "version");
        var publishDir = Path.GetFullPath(Required(options, "publish-dir"));
        var outputDir = Path.GetFullPath(Required(options, "output-dir"));
        var privateKeyPath = Required(options, "private-key");
        var keyId = Required(options, "key-id");
        var packageUrl = Required(options, "package-url");
        var bootstrapperUrl = Required(options, "bootstrapper-url");
        var channel = Optional(options, "channel", "stable");
        var releaseNotes = Optional(options, "release-notes", $"Vellum {version}");
        var packageName = Optional(options, "package-name", $"Vellum-Launcher-{version}.zip");
        var manifestName = Optional(options, "manifest-name", "launcher-manifest.json");
        var bootstrapperName = Optional(options, "bootstrapper-name", "Vellum.Bootstrapper.exe");

        RequireFile(Path.Combine(publishDir, "Vellum.exe"), "Vellum.exe");
        RequireFile(Path.Combine(publishDir, "Vellum.dll"), "Vellum.dll");
        RequireFile(Path.Combine(publishDir, "Vellum.Core.dll"), "Vellum.Core.dll");
        RequireFile(Path.Combine(publishDir, "Vellum.runtimeconfig.json"), "Vellum.runtimeconfig.json");
        RequireFile(Path.Combine(publishDir, "Vellum.deps.json"), "Vellum.deps.json");
        var bootstrapperPath = Path.Combine(publishDir, "Bootstrapper", "Vellum.Bootstrapper.exe");
        RequireFile(bootstrapperPath, "Bootstrapper\\Vellum.Bootstrapper.exe");

        Directory.CreateDirectory(outputDir);
        var packagePath = Path.Combine(outputDir, packageName);
        var manifestPath = Path.Combine(outputDir, manifestName);
        var bootstrapperOutputPath = Path.Combine(outputDir, bootstrapperName);
        DeleteIfExists(packagePath);
        DeleteIfExists(manifestPath);
        DeleteIfExists(bootstrapperOutputPath);

        var files = EnumeratePayloadFiles(publishDir).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("The publish directory has no payload files.");

        var manifestFiles = new List<object>(files.Count);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(publishDir, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);

                var info = new FileInfo(file);
                manifestFiles.Add(new
                {
                    path = relative,
                    size = info.Length,
                    sha256 = ComputeSha256(file)
                });
            }
        }

        var packageInfo = new FileInfo(packagePath);
        var packageSha256 = ComputeSha256(packagePath);
        File.Copy(bootstrapperPath, bootstrapperOutputPath, overwrite: true);
        var bootstrapperInfo = new FileInfo(bootstrapperOutputPath);
        var bootstrapperVersion = FileVersionInfo.GetVersionInfo(bootstrapperOutputPath).FileVersion
            ?? version;
        var bootstrapperSha256 = ComputeSha256(bootstrapperOutputPath);

        var manifest = new
        {
            schemaVersion = SchemaVersion,
            appId = AppId,
            version,
            channel,
            packageUrl,
            packageSize = packageInfo.Length,
            packageSha256,
            entryPoint = "Vellum.exe",
            files = manifestFiles,
            deletedFiles = Array.Empty<string>(),
            releaseNotes,
            releaseDate = DateTimeOffset.UtcNow,
            bootstrapper = new
            {
                version = bootstrapperVersion,
                url = bootstrapperUrl,
                size = bootstrapperInfo.Length,
                sha256 = bootstrapperSha256
            }
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var envelope = new
        {
            schemaVersion = SchemaVersion,
            keyId,
            signature = Convert.ToBase64String(signature),
            payload = Convert.ToBase64String(payload)
        };
        File.WriteAllBytes(manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));

        Console.WriteLine($"VERSION={version}");
        Console.WriteLine($"PACKAGE={packagePath}");
        Console.WriteLine($"PACKAGE_SIZE={packageInfo.Length}");
        Console.WriteLine($"PACKAGE_SHA256={packageSha256}");
        Console.WriteLine($"MANIFEST={manifestPath}");
        Console.WriteLine($"FILES={manifestFiles.Count}");
        Console.WriteLine($"BOOTSTRAPPER={bootstrapperOutputPath}");
        Console.WriteLine($"BOOTSTRAPPER_VERSION={bootstrapperVersion}");
        Console.WriteLine($"BOOTSTRAPPER_SIZE={bootstrapperInfo.Length}");
        Console.WriteLine($"BOOTSTRAPPER_SHA256={bootstrapperSha256}");
        return 0;
    }

    private static int Verify(Dictionary<string, string> options)
    {
        var manifestPath = Required(options, "manifest");
        var publicKeyPath = Required(options, "public-key");

        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = document.RootElement;
        var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()
            ?? throw new InvalidOperationException("The manifest payload is missing."));
        var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()
            ?? throw new InvalidOperationException("The manifest signature is missing."));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("The manifest signature is invalid.");

        using var payloadDocument = JsonDocument.Parse(payload);
        var manifest = payloadDocument.RootElement;
        Console.WriteLine($"SIGNATURE=valid");
        Console.WriteLine($"KEY_ID={root.GetProperty("keyId").GetString()}");
        Console.WriteLine($"VERSION={manifest.GetProperty("version").GetString()}");
        Console.WriteLine($"PACKAGE_SIZE={manifest.GetProperty("packageSize").GetInt64()}");
        Console.WriteLine($"FILES={manifest.GetProperty("files").GetArrayLength()}");
        return 0;
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Equals("Bootstrapper", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Bootstrapper/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return file;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RequireFile(string path, string displayName)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required file is missing: {displayName}", path);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            var name = arg[2..];
            if (name.Length == 0)
                throw new ArgumentException("An option name is missing.");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option '--{name}' needs a value.");
            options[name] = args[++i];
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required option '--{name}'.");
        return value;
    }

    private static string Optional(Dictionary<string, string> options, string name, string fallback)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Vellum release feed tool

            keygen:
              --private <path> --public <path> --key-id <id>

            build:
              --version <version>
              --publish-dir <dir>
              --output-dir <dir>
              --private-key <pem>
              --key-id <id>
              --package-url <url>
              --bootstrapper-url <url>
              [--channel stable]
              [--release-notes text]
              [--package-name name]
              [--manifest-name name]
              [--bootstrapper-name name]

            verify:
              --manifest <launcher-manifest.json> --public-key <pem>
            """);
    }
}
