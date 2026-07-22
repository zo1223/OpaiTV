using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace OrionTV.Desktop.App.LiveTranslation;

/// <summary>
/// 统一读写翻译 API Key（OpenAI / Gemini 分槽保存在 Windows 凭据管理器）。
/// </summary>
internal static class OpenAiCredentialStore
{
    private const string OpenAiTarget = "OrionTV.Desktop/OpenAI";
    private const string GeminiTarget = "OrionTV.Desktop/Gemini";
    private const string DoubaoTarget = "OrionTV.Desktop/Doubao";
    private const string DeepgramTarget = "OrionTV.Desktop/Deepgram";
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string? ReadApiKey() => ReadApiKeyForProvider("openai");

    public static string? ReadApiKeyForProvider(string? provider)
    {
        var isGemini = string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase);
        var isDoubao = string.Equals(provider, "doubao", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volcengine", StringComparison.OrdinalIgnoreCase);
        var isDeepgram = string.Equals(provider, "deepgram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova-3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova3", StringComparison.OrdinalIgnoreCase);

        if (isGemini)
        {
            var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim()
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")?.Trim();
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return ReadStored(GeminiTarget);
        }

        if (isDoubao)
        {
            var env = Environment.GetEnvironmentVariable("DOUBAO_ACCESS_KEY")?.Trim()
                ?? Environment.GetEnvironmentVariable("VOLC_ACCESS_KEY")?.Trim();
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return ReadStored(DoubaoTarget);
        }

        if (isDeepgram)
        {
            var env = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY")?.Trim();
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return ReadStored(DeepgramTarget);
        }

        var openAiEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        return !string.IsNullOrWhiteSpace(openAiEnv) ? openAiEnv : ReadStored(OpenAiTarget);
    }

    public static bool HasApiKey() => !string.IsNullOrWhiteSpace(ReadApiKey());

    public static bool HasStoredApiKey() => HasStoredApiKeyForProvider("openai");

    public static bool HasStoredApiKeyForProvider(string? provider)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(ReadApiKeyForProvider(provider));
        }
        catch
        {
            return false;
        }
    }

    public static void SaveApiKey(string apiKey) => SaveApiKeyForProvider("openai", apiKey);

    public static void SaveApiKeyForProvider(string? provider, string apiKey)
    {
        var value = apiKey.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("API Key 不能为空", nameof(apiKey));
        }

        var isGemini = string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase);
        var isDoubao = string.Equals(provider, "doubao", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volcengine", StringComparison.OrdinalIgnoreCase);
        var isDeepgram = string.Equals(provider, "deepgram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova-3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova3", StringComparison.OrdinalIgnoreCase);
        var target = isGemini
            ? GeminiTarget
            : isDoubao
                ? DoubaoTarget
                : isDeepgram
                    ? DeepgramTarget
                    : OpenAiTarget;
        WriteStored(target, value);
    }

    public static bool DeleteStoredApiKey() => DeleteStored(OpenAiTarget);

    public static bool DeleteStoredApiKeyForProvider(string? provider)
    {
        var isGemini = string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase);
        var isDoubao = string.Equals(provider, "doubao", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "volcengine", StringComparison.OrdinalIgnoreCase);
        var isDeepgram = string.Equals(provider, "deepgram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova-3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "nova3", StringComparison.OrdinalIgnoreCase);
        var target = isGemini
            ? GeminiTarget
            : isDoubao
                ? DoubaoTarget
                : isDeepgram
                    ? DeepgramTarget
                    : OpenAiTarget;
        return DeleteStored(target);
    }

    private static string? ReadStored(string target)
    {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "无法读取 Windows 凭据管理器");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void WriteStored(string target, string value)
    {
        var blob = Encoding.Unicode.GetBytes(value);
        var blobPointer = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows 凭据管理器");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            if (blobPointer != IntPtr.Zero)
            {
                var zeros = new byte[blob.Length];
                Marshal.Copy(zeros, 0, blobPointer, zeros.Length);
                Marshal.FreeCoTaskMem(blobPointer);
            }
        }
    }

    private static bool DeleteStored(string target)
    {
        if (CredDelete(target, CredentialTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, "无法删除 Windows 凭据");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
