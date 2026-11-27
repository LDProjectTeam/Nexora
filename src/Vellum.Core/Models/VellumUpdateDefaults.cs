namespace Vellum.Core.Models
{
    /// <summary>
    /// Publisher-controlled defaults for the signed GitHub Releases update feed.
    /// The private signing key never ships with the application.
    /// </summary>
    public static class VellumUpdateDefaults
    {
        public const string KeyId = "vellum-release-2026";

        public const string ManifestUrl =
            "https://github.com/LDProjectTeam/Vellum/releases/latest/download/launcher-manifest.json";

        public const string ReleasePublicKey = """
# key-id: vellum-release-2026
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAs7crDR3tpNXQk8tBhzz4
aqDIsC0qmnWYe2MpPSqZQsgSQN4petV+fUXL0yZktNNIIiYYw15JYgPUxca1XQ8T
jcjGBo+KvdnBh8kAY/wwWenguLhYjJJ6WizGTukB505kJKMYiRn860H14RLmzlAi
PWxiSo8jUriqaym9Sl3/VYBr148VmFjtOeEsSzVst6a3HGvgtycfXx25GvG/2kHT
9Ur5/KfvsflD4HRKEFklrSW5ivTERl1uLNxEJIzavMiPCYZzQ9vjx6waS+FNF286
T20Mk2p42rNwN3KkfDbsoS0fdrmLJMluCdGNZ2bkNnSG/sW4sWxII7oxdXSCcc6O
0AJbitZjWfqVUyo2zD13KJziuCrqTbQlZo5rPtQAK8JeWEd/irzdMSx4o7l4ap41
hD6H4IwA0UMCOEeOPVBfzpC2x6fNh15SvL9j+RGBpnWgcDjpNQtFS8z8D62nJBqM
coqIfMbLr4mnRefKQ+v6ImM0hLpPuSzbLcBwf5PeD9eFAgMBAAE=
-----END PUBLIC KEY-----
""";
    }
}
