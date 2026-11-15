using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DotNetAgentSurface.Core;

namespace DotNetAgentSurface.Hosting;

/// <summary>
/// An <see cref="IAgentSession"/> persisted as a small JSON file so state survives across CLI invocations. It is loaded
/// by <see cref="Open"/> and written by <see cref="Save"/>, which replaces the file atomically (temp file plus rename);
/// concurrent writers are last-writer-wins. Never store secrets in it: the file is plain JSON.
/// </summary>
public sealed class FileAgentSession : InMemoryAgentSession
{
    private const int MaxIdLength = 128;
    private const int MoveAttempts = 200;

    private FileAgentSession(string id, string path)
        : base(id)
    {
        Path = path;
    }

    /// <summary>Gets the full path of the session file (which may not exist yet).</summary>
    public string Path { get; }

    /// <summary>Gets when the session file was last written, or <see langword="null"/> when it does not exist.</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>Gets a copy of the stored values.</summary>
    public IReadOnlyDictionary<string, JsonElement> Values => Snapshot();

    /// <summary>
    /// Gets the directory that holds session files for <paramref name="appName"/>: <c>&lt;root&gt;/&lt;app&gt;/sessions</c>, where
    /// the root is <paramref name="stateDirectory"/>, <c>%LOCALAPPDATA%</c> on Windows, or <c>$XDG_STATE_HOME</c> (falling
    /// back to <c>~/.local/state</c>) elsewhere.
    /// </summary>
    public static string GetSessionsDirectory(string appName, string? stateDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new ArgumentException("An application name is required.", nameof(appName));
        }

        string root;
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            root = stateDirectory;
        }
        else if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            root = !string.IsNullOrWhiteSpace(xdg) && System.IO.Path.IsPathRooted(xdg)
                ? xdg
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        }

        return System.IO.Path.Combine(root, appName, "sessions");
    }

    /// <summary>
    /// Derives the default session id for a working directory: a short hash, so every workspace gets its own state.
    /// </summary>
    public static string CreateWorkingDirectoryId(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
        }

        var normalized = System.IO.Path.GetFullPath(workingDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        var builder = new StringBuilder("cwd-");
        for (var index = 0; index < 8; index++)
        {
            builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Returns whether <paramref name="id"/> is usable as a session id: 1-128 characters of letters, digits, <c>.</c>, <c>_</c> or <c>-</c>.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id is "." or "..")
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Opens the session <paramref name="id"/> from <paramref name="sessionsDirectory"/>. A missing or unreadable file yields an
    /// empty session; nothing is written until <see cref="Save"/>.
    /// </summary>
    public static FileAgentSession Open(string sessionsDirectory, string id)
    {
        if (string.IsNullOrWhiteSpace(sessionsDirectory))
        {
            throw new ArgumentException("A sessions directory is required.", nameof(sessionsDirectory));
        }

        if (!IsValidId(id))
        {
            throw new ArgumentException(
                $"'{id}' is not a valid session id. Use 1-{MaxIdLength} letters, digits, '.', '_' or '-'.",
                nameof(id));
        }

        var session = new FileAgentSession(id, System.IO.Path.Combine(sessionsDirectory, id + ".json"));
        session.Load();
        return session;
    }

    /// <summary>Deletes session files in <paramref name="sessionsDirectory"/> not written within <paramref name="timeToLive"/>, except <paramref name="keepId"/>. Best effort.</summary>
    public static void PruneExpired(string sessionsDirectory, TimeSpan timeToLive, string? keepId = null)
    {
        if (timeToLive <= TimeSpan.Zero || !Directory.Exists(sessionsDirectory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - timeToLive;
        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
            {
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), keepId, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Marks an existing session file as recently used so read-only sessions are not pruned while in use. Best effort.</summary>
    public void Touch()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.SetLastWriteTimeUtc(Path, DateTime.UtcNow);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
    /// <summary>Writes the session if it changed since it was loaded. Returns whether a file was written.</summary>
    public bool Save()
    {
        if (!IsModified)
        {
            return false;
        }

        var directory = System.IO.Path.GetDirectoryName(Path)!;
        EnsureDirectory(directory);

        var updatedAt = DateTimeOffset.UtcNow;
        var values = Snapshot(out var version);
        var temporaryPath = System.IO.Path.Combine(directory, $".{Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateTemporaryFile(temporaryPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("id", Id);
                writer.WriteString("updatedAt", updatedAt);
                writer.WriteStartObject("values");
                foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    pair.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            MoveIntoPlace(temporaryPath, Path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        UpdatedAt = updatedAt;
        MarkClean(version);
        return true;
    }

    /// <summary>Removes all values and deletes the session file. Returns whether a file was deleted.</summary>
    public bool Clear()
    {
        Replace([]);
        UpdatedAt = null;
        if (!File.Exists(Path))
        {
            return false;
        }

        File.Delete(Path);
        return true;
    }

    private void Load()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (document.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                Replace(values.EnumerateObject().Select(static property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)));
            }

            if (document.RootElement.TryGetProperty("updatedAt", out var updated)
                && updated.ValueKind == JsonValueKind.String
                && updated.TryGetDateTimeOffset(out var updatedAt))
            {
                UpdatedAt = updatedAt;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static FileStream CreateTemporaryFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateRestrictedFile(path);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateRestrictedFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    private static void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var created = Directory.CreateDirectory(directory);
            RestrictToCurrentUser(created);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Files created in the directory inherit this ACL, so the session JSON is readable by the current user only.
    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    // Replacing a file another process is reading or replacing can fail transiently on Windows.
    private static void MoveIntoPlace(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < MoveAttempts && OperatingSystem.IsWindows() && IsTransientMoveFailure(exception))
            {
                Thread.Sleep(Random.Shared.Next(1, 4 + Math.Min(attempt, 30)));
            }
        }
    }

    // 0x80070020 ERROR_SHARING_VIOLATION and 0x80070021 ERROR_LOCK_VIOLATION; a denied replace surfaces as UnauthorizedAccessException.
    private static bool IsTransientMoveFailure(Exception exception) =>
        exception is UnauthorizedAccessException
        || (exception is IOException io && io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
