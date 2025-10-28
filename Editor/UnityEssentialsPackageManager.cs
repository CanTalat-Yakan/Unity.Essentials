using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityEssentials.UnityEssentials.Editor
{
    public class UnityEssentialsPackageManager : MonoBehaviour
    {
        private const string GithubUser = "CanTalat-Yakan";
        private const string MenuPath = "Tools/Install & Update UnityEssentials";
        private const int HttpTimeoutSeconds = 30;

        [MenuItem(MenuPath, priority = -10000)]
        public static void InstallUnityEssentials()
        {
            try
            {
                // Ask whether to include Unity.Templates.* repos (these may contain large assets)
                bool includeTemplates = EditorUtility.DisplayDialog(
                    "UnityEssentials Installer",
                    "Do you want to include Unity.Templates.* repositories?\nThese often contain large assets and textures and may take longer to download.",
                    "Include",
                    "Skip");

                EditorUtility.DisplayProgressBar("UnityEssentials Installer", "Querying GitHub repositories…", 0f);

                // Get installed packages list once to detect install vs update
                var listReq = Client.List();
                while (!listReq.IsCompleted)
                {
                    Thread.Sleep(20);
                }
                var installedByName = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>(StringComparer.OrdinalIgnoreCase);
                if (listReq.Status == StatusCode.Success && listReq.Result != null)
                {
                    foreach (var p in listReq.Result)
                    {
                        if (!string.IsNullOrEmpty(p.name) && !installedByName.ContainsKey(p.name))
                            installedByName.Add(p.name, p);
                    }
                }

                var repos = FetchAllRepos(GithubUser);
                if (repos == null)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("UnityEssentials Installer", "Failed to retrieve repositories from GitHub. Check your internet connection or try again later.", "OK");
                    return;
                }

                var candidates = new List<Repo>();
                foreach (var r in repos)
                {
                    if (r == null || string.IsNullOrEmpty(r.name)) continue;
                    if (!r.name.StartsWith("Unity.", StringComparison.Ordinal)) continue;
                    // Optionally skip template repositories
                    if (!includeTemplates && r.name.StartsWith("Unity.Templates.", StringComparison.Ordinal))
                        continue;
                    candidates.Add(r);
                }

                if (candidates.Count == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("UnityEssentials Installer", "No repositories with prefix 'Unity.' found for the user " + GithubUser + ".", "OK");
                    return;
                }

                int installedCount = 0;
                int updatedCount = 0;
                int upToDateCount = 0;
                var successes = new List<string>();
                var failures = new List<string>();

                for (int i = 0; i < candidates.Count; i++)
                {
                    var repo = candidates[i];
                    float progress = (float)i / Math.Max(1, candidates.Count);
                    EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"Checking {repo.name} ({i + 1}/{candidates.Count})…", progress);

                    // Try to resolve package name from package.json on default branch
                    if (!TryGetPackageNameFromRepo(repo, out var packageName))
                    {
                        failures.Add($"{repo.name}: package.json missing or invalid on branch {repo.default_branch}");
                        continue;
                    }

                    bool isInstalled = installedByName.ContainsKey(packageName);
                    string action = isInstalled ? "Updating" : "Installing";

                    // Install/update via UPM from git; pin to default branch
                    var gitUrl = $"https://github.com/{repo.owner?.login}/{repo.name}.git#{repo.default_branch}";
                    EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"{action} {packageName}…", progress);

                    // Keep previous packageId to detect if update actually changed anything
                    string prevPackageId = null;
                    if (isInstalled && installedByName.TryGetValue(packageName, out var prevInfo))
                        prevPackageId = prevInfo.packageId;

                    var addReq = Client.Add(gitUrl);
                    var start = DateTime.UtcNow;
                    while (!addReq.IsCompleted)
                    {
                        var elapsed = (float)(DateTime.UtcNow - start).TotalSeconds;
                        EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"{action} {packageName}… ({elapsed:0}s)", progress + 0.001f * (i + 1));
                        Thread.Sleep(50);
                    }

                    if (addReq.Status == StatusCode.Success)
                    {
                        var resultName = addReq.Result?.name ?? packageName;
                        var newPackageId = addReq.Result?.packageId ?? gitUrl;

                        if (isInstalled)
                        {
                            if (!string.IsNullOrEmpty(prevPackageId) && !string.IsNullOrEmpty(newPackageId) && !string.Equals(prevPackageId, newPackageId, StringComparison.Ordinal))
                            {
                                updatedCount++;
                                successes.Add(resultName + " updated -> " + newPackageId);
                            }
                            else
                            {
                                upToDateCount++;
                                successes.Add(resultName + " already up-to-date");
                            }
                        }
                        else
                        {
                            installedCount++;
                            successes.Add(resultName + " installed -> " + newPackageId);
                        }

                        // refresh installed cache entry
                        if (!string.IsNullOrEmpty(addReq.Result?.name))
                            installedByName[addReq.Result.name] = addReq.Result;
                    }
                    else
                    {
                        string err = addReq.Error != null ? addReq.Error.message : "Unknown error";
                        failures.Add($"{packageName}: {err}");
                    }
                }

                EditorUtility.ClearProgressBar();

                var sb = new StringBuilder();
                sb.AppendLine("Install/Update complete.");
                sb.AppendLine($"Candidates: {candidates.Count}");
                sb.AppendLine($"Installed: {installedCount}");
                sb.AppendLine($"Updated: {updatedCount}");
                if (upToDateCount > 0) sb.AppendLine($"Up-to-date: {upToDateCount}");
                if (successes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Successes:");
                    foreach (var s in successes) sb.AppendLine("  • " + s);
                }
                if (failures.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Failures:");
                    foreach (var f in failures) sb.AppendLine("  • " + f);
                }

                Debug.Log(sb.ToString());
                var shortSummary =
                    $"Found {candidates.Count} candidate repos with prefix 'Unity.'{(includeTemplates ? " (including templates)" : " (templates skipped)")}.\nInstalled {installedCount}, Updated {updatedCount}, Up-to-date {upToDateCount}, Failed {failures.Count}.";
                EditorUtility.DisplayDialog("UnityEssentials Installer", shortSummary, "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("UnityEssentials Installer failed: " + ex);
                EditorUtility.DisplayDialog("UnityEssentials Installer", "An unexpected error occurred: " + ex.Message, "OK");
            }
        }

        // --- Helpers ---

        [Serializable]
        private class Owner { public string login; }

        [Serializable]
        private class Repo
        {
            public string name;
            public string default_branch;
            public Owner owner;
        }

        [Serializable]
        private class RepoListWrapper { public Repo[] items; }

        [Serializable]
        private class PackageJson { public string name; }

        private static bool TryGetPackageNameFromRepo(Repo repo, out string packageName)
        {
            packageName = null;
            if (repo == null || string.IsNullOrEmpty(repo.name) || string.IsNullOrEmpty(repo.default_branch)) return false;
            var packageJsonUrl = $"https://raw.githubusercontent.com/{repo.owner?.login}/{repo.name}/{repo.default_branch}/package.json";
            var json = HttpGet(packageJsonUrl, out long code, out var _);
            if (code < 200 || code >= 300 || string.IsNullOrEmpty(json)) return false;
            try
            {
                var pj = JsonUtility.FromJson<PackageJson>(json);
                if (pj == null || string.IsNullOrEmpty(pj.name)) return false;
                packageName = pj.name;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Repo> FetchAllRepos(string user)
        {
            var all = new List<Repo>();
            int page = 1;
            while (page < 20) // reasonable safety upper bound
            {
                string url = $"https://api.github.com/users/{user}/repos?per_page=100&page={page}";
                var json = HttpGet(url, out long code, out Dictionary<string, string> headers);
                if (code == 403 && headers != null && headers.TryGetValue("x-ratelimit-remaining", out var remaining) && remaining == "0")
                {
                    Debug.LogError("GitHub rate limit exceeded. Try again later or authenticate.");
                    return null;
                }
                if (string.IsNullOrEmpty(json)) break;

                // GitHub returns a JSON array; wrap it so JsonUtility can parse
                var wrapped = "{\"items\":" + json + "}";
                RepoListWrapper wrapper = null;
                try
                {
                    wrapper = JsonUtility.FromJson<RepoListWrapper>(wrapped);
                }
                catch (Exception e)
                {
                    Debug.LogError("Failed to parse GitHub response: " + e);
                    return null;
                }

                if (wrapper?.items == null || wrapper.items.Length == 0) break;
                all.AddRange(wrapper.items);
                page++;
            }
            return all;
        }

        private static string HttpGet(string url, out long responseCode, out Dictionary<string, string> responseHeaders)
        {
            responseHeaders = null;
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = HttpTimeoutSeconds;
                req.SetRequestHeader("User-Agent", "UnityEssentialsPackageInstaller");
                var op = req.SendWebRequest();
                var start = DateTime.UtcNow;
                while (!op.isDone)
                {
                    // avoid busy wait
                    Thread.Sleep(10);
                    if ((DateTime.UtcNow - start).TotalSeconds > HttpTimeoutSeconds + 5)
                    {
                        break;
                    }
                }

#if UNITY_2020_2_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                responseCode = req.responseCode;
                try
                {
                    // Collect headers to detect rate-limits etc.
                    responseHeaders = req.GetResponseHeaders();
                }
                catch
                {
                    // ignore
                }
                return ok ? req.downloadHandler.text : null;
            }
        }
    }
}
