using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LlamaPaddock.Agent
{
    /// <summary>Small Windows helpers for the installer: commands, user profiles, ACLs, JSON files.</summary>
    static class Win
    {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        /// <summary>Run a program with its arguments quoted one by one (never through a shell) → exit code and output.</summary>
        public static (int code, string output) Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(QuoteArg)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, (output + err.Result).Trim());
            }
        }

        /// <summary>The CommandLineToArgvW rules: quote when needed, double backslashes before a quote.</summary>
        static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        public sealed class Profile
        {
            public string Sid;
            public string Dir;
            public string LocalAppData => Path.Combine(Dir, "AppData", "Local");
            public string Startup => Path.Combine(Dir, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        }

        /// <summary>The PC's user profiles (not service or system ones), from the registry's profile list.</summary>
        public static List<Profile> Profiles()
        {
            var list = new List<Profile>();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
            {
                if (key == null) return list;
                foreach (string sid in key.GetSubKeyNames())
                {
                    // S-1-5-21-…: domain and local accounts
                    if (!sid.StartsWith("S-1-5-21-") || sid.EndsWith(".bak")) continue;
                    using (var sub = key.OpenSubKey(sid))
                    {
                        string dir = Environment.ExpandEnvironmentVariables(sub?.GetValue("ProfileImagePath") as string ?? "");
                        if (dir != "" && Directory.Exists(dir)) list.Add(new Profile { Sid = sid, Dir = dir });
                    }
                }
            }
            return list;
        }

        public static Profile ProfileOf(string path) =>
            path == null ? null : Profiles().FirstOrDefault(p => path.StartsWith(p.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public static Profile CurrentProfile()
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value;
            return Profiles().FirstOrDefault(p => p.Sid == sid);
        }

        /// <summary>A user's environment variables, when their registry hive is loaded (they're logged in); else null.</summary>
        public static Dictionary<string, string> UserEnvironment(string sid)
        {
            using (var key = Registry.Users.OpenSubKey(sid + @"\Environment"))
            {
                if (key == null) return null;
                return key.GetValueNames().ToDictionary(
                    n => n,
                    n => key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "",
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        // Grants on folders the installer doesn't own go through icacls. .NET's SetAccessControl writes the
        // whole list, and Windows then re-applies inheritance to everything below: for a user's profile
        // folder that is every file in it (around a million on a used PC), which took many minutes.
        // icacls changes only the entry, and walks the subtree only for an inheritable one.

        /// <summary>Read and execute, or modify, on the folder and everything in it.</summary>
        public static void Grant(string path, IdentityReference who, FileSystemRights rights) =>
            Icacls(path, "/grant", $"*{Sid(who)}:(OI)(CI){(rights == FileSystemRights.Modify ? "M" : "RX")}");

        /// <summary>
        /// Open and list the folder itself, not its files or subfolders. Read and execute rather than just
        /// list: Go opens a folder for generic read, which also needs its permissions and extended attributes.
        /// </summary>
        public static void GrantList(string path, IdentityReference who) => Icacls(path, "/grant", $"*{Sid(who)}:(RX)");

        public static void Revoke(string path, IdentityReference who)
        {
            if (Directory.Exists(path)) Icacls(path, "/remove", $"*{Sid(who)}");
        }

        static string Sid(IdentityReference who) => who.Translate(typeof(SecurityIdentifier)).Value;

        static void Icacls(string path, string action, string entry)
        {
            var (code, output) = Run("icacls.exe", path, action, entry, "/C", "/Q");
            if (code != 0) throw new InvalidOperationException($"icacls {action} on {path}: {output}");
        }

        /// <summary>Only SYSTEM, Administrators and the service: the folder holds the token and the TLS key.</summary>
        public static void MakePrivate(string path, IdentityReference service)
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path).SetAccessControl(acl);
            // files already there (from an earlier version) take the folder's rules
            foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                var fs = File.GetAccessControl(f);
                fs.SetAccessRuleProtection(false, false);
                foreach (FileSystemAccessRule r in fs.GetAccessRules(true, false, typeof(SecurityIdentifier))) fs.RemoveAccessRuleSpecific(r);
                File.SetAccessControl(f, fs);
            }
        }

        public static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            return ReadJsonText(File.ReadAllText(path)) ?? new Dictionary<string, object>();
        }

        // older installers wrote a UTF-8 BOM
        public static Dictionary<string, object> ReadJsonText(string text) => Json.Deserialize<Dictionary<string, object>>(text.TrimStart('﻿'));

        public static void WriteJson(string path, object value) => File.WriteAllText(path, Json.Serialize(value), Utf8);

        public static string ToJson(object value) => Json.Serialize(value);

        public static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
