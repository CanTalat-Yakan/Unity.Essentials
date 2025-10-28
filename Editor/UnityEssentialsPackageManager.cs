using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityEssentials.UnityEssentials.Editor
{
    public class UnityEssentialsPackageManager : MonoBehaviour
    {
        private const string GithubUser = "CanTalat-Yakan";
        private const string MenuPath = "Tools/Install UnityEssentials";
        private const int HttpTimeoutSeconds = 30;

        [MenuItem(MenuPath, priority = -10000)]
        public static void InstallUnityEssentials()
        {
            try
            {
                EditorUtility.DisplayProgressBar("UnityEssentials Installer", "Querying GitHub repositories…", 0f);

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
                    if (r.name.StartsWith("Unity.", StringComparison.Ordinal))
                        candidates.Add(r);
                }

                if (candidates.Count == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("UnityEssentials Installer", "No repositories with prefix 'Unity.' found for the user " + GithubUser + ".", "OK");
                    return;
                }

                var successes = new List<string>();
                var failures = new List<string>();

                for (int i = 0; i < candidates.Count; i++)
                {
                    var repo = candidates[i];
                    float progress = (float)i / Math.Max(1, candidates.Count);
                    EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"Checking {repo.name} ({i + 1}/{candidates.Count})…", progress);

                    // Verify package.json exists at root on default branch
                    var packageJsonUrl = $"https://raw.githubusercontent.com/{repo.owner?.login}/{repo.name}/{repo.default_branch}/package.json";
                    if (!UrlExists(packageJsonUrl))
                    {
                        failures.Add($"{repo.name}: package.json not found on branch {repo.default_branch}");
                        continue;
                    }

                    // Install via UPM from git; pin to default branch
                    var gitUrl = $"https://github.com/{repo.owner?.login}/{repo.name}.git#{repo.default_branch}";
                    EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"Installing {repo.name}…", progress);

                    var addReq = Client.Add(gitUrl);
                    var start = DateTime.UtcNow;
                    while (!addReq.IsCompleted)
                    {
                        // Update progress a little while waiting
                        var elapsed = (float)(DateTime.UtcNow - start).TotalSeconds;
                        EditorUtility.DisplayProgressBar("UnityEssentials Installer", $"Installing {repo.name}… ({elapsed:0}s)", progress + 0.001f * (i + 1));
                        Thread.Sleep(50);
                    }

                    if (addReq.Status == StatusCode.Success)
                    {
                        successes.Add(repo.name + " -> " + addReq.Result?.packageId);
                    }
                    else
                    {
                        string err = addReq.Error != null ? addReq.Error.message : "Unknown error";
                        failures.Add($"{repo.name}: {err}");
                    }
                }

                EditorUtility.ClearProgressBar();

                var sb = new StringBuilder();
                sb.AppendLine("Install complete.");
                if (successes.Count > 0)
                {
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
                EditorUtility.DisplayDialog("UnityEssentials Installer", sb.ToString(), "OK");
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

        private static bool UrlExists(string url)
        {
            var _ = HttpGet(url, out long code, out var headers);
            return code >= 200 && code < 300;
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
