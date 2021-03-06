﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using LibGit2Sharp;
using UniGit.Security;
using UniGit.Status;
using UniGit.Utils;
using UnityEditor;
using UnityEngine;
using Utils.Extensions;

namespace UniGit
{
	public static class GitManager
	{
		public const string Version = "1.1.0";

		private static string repoPathCached;
		private static string gitPathCached;
		public static string RepoPath { get { return repoPathCached; } }

		private static Repository repository;
		private static StatusTreeClass statusTree;
		private static GitSettingsJson gitSettings;
		private static bool needsFetch;
		private readonly static Queue<Action> actionQueue = new Queue<Action>();
		private static GitRepoStatus status;
		private static readonly object statusTreeLock = new object();
		private static readonly object statusRetriveLock = new object();
		private static bool repositoryDirty;
		private static bool reloadDirty;
		private static bool isUpdating;
		private static readonly HashSet<string> dirtyFiles = new HashSet<string>();
		private static readonly List<string> updatingFiles = new List<string>();

		[InitializeOnLoadMethod]
		internal static void Initlize()
		{
			repoPathCached = Application.dataPath.Replace("/Assets", "").Replace("/", "\\");
			gitPathCached = Application.dataPath.Replace("Assets", ".git");

			if (!IsValidRepo)
			{
				return;
			}

			LoadGitSettings();

			GitResourceManager.Initilize();
			GitCredentialsManager.Load();
			GitOverlay.Initlize();
			GitLfsManager.Load();
			GitHookManager.Load();
			GitExternalManager.Load();

			needsFetch = !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
			repositoryDirty = true;
			GitCallbacks.EditorUpdate += OnEditorUpdate;
		}

		internal static void InitilizeRepository()
		{
			Repository.Init(Application.dataPath.Replace("/Assets", ""));
			string newGitIgnoreFile = Path.Combine(Application.dataPath.Replace("Assets", "").Replace("Contents", ""), ".gitignore");
			if (!File.Exists(newGitIgnoreFile))
			{
				File.WriteAllText(Path.Combine(Application.dataPath, newGitIgnoreFile), GitIgnoreTemplate.Template);
			}
			else
			{
				Debug.Log("Git Ignore file already present");
			}
			AssetDatabase.Refresh();
			AssetDatabase.SaveAssets();
			Initlize();
			MarkDirty();
		}

		private static void SaveSettingsToFile(GitSettingsJson settings)
		{
			ValidateSettingsPath();
			string settingsFilePath = SettingsFilePath;

			try
			{
				string json = JsonUtility.ToJson(settings);
				File.WriteAllText(settingsFilePath, json);
			}
			catch (Exception e)
			{
				Debug.LogError("Could not serialize GitSettingsJson to json file at: " + settingsFilePath);
				Debug.LogException(e);
			}
		}

		private static void ValidateSettingsPath()
		{
			string settingsFileDirectory = Path.Combine(gitPathCached,"UniGit");
			if (!Directory.Exists(settingsFileDirectory))
			{
				Directory.CreateDirectory(settingsFileDirectory);
			}
		}

		private static void LoadGitSettings()
		{
			string settingsFilePath = SettingsFilePath;
			GitSettingsJson settingsJson = null;
			if (File.Exists(settingsFilePath))
			{
				try
				{
					settingsJson = JsonUtility.FromJson<GitSettingsJson>(File.ReadAllText(settingsFilePath));
				}
				catch (Exception e)
				{
					Debug.LogError("Could not deserialize git settings. Creating new settings.");
					Debug.LogException(e);
				}
			}

			if(settingsJson == null)
			{
				settingsJson = new GitSettingsJson();
				var oldSettingsFile = EditorGUIUtility.Load("UniGit/Git-Settings.asset") as GitSettings;
				if (oldSettingsFile != null)
				{
					//must be delayed call for unity to deserialize settings file properly
					EditorApplication.delayCall += LoadOldSettingsFile;
				}
				else
				{
					SaveSettingsToFile(settingsJson);
				}
			}

			gitSettings = settingsJson;
		}

		private static void LoadOldSettingsFile()
		{
			var oldSettingsFile = EditorGUIUtility.Load("UniGit/Git-Settings.asset") as GitSettings;
			if (oldSettingsFile != null)
			{
				gitSettings.Copy(oldSettingsFile);
				Debug.Log("Old Git Settings transferred to new json settings file. Old settings can now safely be removed.");
			}
			SaveSettingsToFile(gitSettings);
			MarkDirty(true);
		}

		internal static void OnEditorUpdate()
		{
			if (gitSettings.IsDirty)
			{
				SaveSettingsToFile(gitSettings);
				gitSettings.ResetDirty();
			}

			if (needsFetch)
			{
				try
				{
					needsFetch = AutoFetchChanges();
				}
				catch(Exception e)
				{
					Debug.LogException(e);
					needsFetch = false;
				}
			}

			if (IsValidRepo && !(EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !isUpdating)
			{
				if ((repository == null || repositoryDirty))
				{
					Update(reloadDirty);
					reloadDirty = false;
					repositoryDirty = false;
					dirtyFiles.Clear();
				}
				else if (dirtyFiles.Count > 0)
				{
					Update(reloadDirty || repository == null, dirtyFiles.ToArray());
					dirtyFiles.Clear();
				}
			}

			if (actionQueue.Count > 0)
			{
				Action action = actionQueue.Dequeue();
				if (action != null)
				{
					try
					{
						action.Invoke();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
						throw;
					}
				}
			}
		}

		private static void Update(bool reloadRepository,string[] paths = null)
		{
			StartUpdating(paths);

			if ((repository == null || reloadRepository) && IsValidRepo)
			{
				if (repository != null) repository.Dispose();
				repository = new Repository(RepoPath);
				GitCallbacks.IssueOnRepositoryLoad(repository);
			}

			if (repository != null)
			{
				if (Settings.GitStatusMultithreaded)
				{
					ThreadPool.QueueUserWorkItem(ReteriveStatusThreaded, paths);
				}
				else
				{
					RetreiveStatus(paths);
				}
			}
		}

		public static void MarkDirty()
		{
			repositoryDirty = true;
		}

		public static void MarkDirty(bool reloadRepo)
		{
			repositoryDirty = true;
			reloadDirty = reloadRepo;
		}

		public static void MarkDirty(string[] paths)
		{
			MarkDirty((IEnumerable<string>)paths);
		}

		public static void MarkDirty(IEnumerable<string> paths)
		{
			foreach (var path in paths)
			{
				string fixedPath = path.Replace("/", "\\");
				if(!dirtyFiles.Contains(fixedPath))
					dirtyFiles.Add(fixedPath);
			}
			
		}

		private static void RebuildStatus(string[] paths)
		{
			if (paths != null && paths.Length > 0 && status != null)
			{
				foreach (var path in paths)
				{
					status.Update(path,repository.RetrieveStatus(path));
				}
			}
			else
			{
				var options = GetStatusOptions();
				var s = repository.RetrieveStatus(options);
				status = new GitRepoStatus(s);
			}
			
		}

		private static StatusOptions GetStatusOptions()
		{
			return new StatusOptions()
			{
				DetectRenamesInIndex = Settings.DetectRenames,
				DetectRenamesInWorkDir = Settings.DetectRenames
			};
		}

		private static void RetreiveStatus(string[] paths)
		{
			try
			{
				GitProfilerProxy.BeginSample("Git Repository Status Retrieval");
				RebuildStatus(paths);
				GitProfilerProxy.EndSample();
				GitCallbacks.IssueUpdateRepository(status, paths);
				ThreadPool.QueueUserWorkItem(UpdateStatusTreeThreaded, status);
			}
			catch (Exception e)
			{
				FinishUpdating();
				Debug.LogError("Could not retrive Git Status");
				Debug.LogException(e);
			}
		}

		private static void ReteriveStatusThreaded(object param)
		{
			Monitor.Enter(statusRetriveLock);
			try
			{
				string[] paths = param as string[];
				RebuildStatus(paths);
				actionQueue.Enqueue(() =>
				{
					GitCallbacks.IssueUpdateRepository(status, paths);
					ThreadPool.QueueUserWorkItem(UpdateStatusTreeThreaded, status);
				});
			}
			catch (ThreadAbortException)
			{
				actionQueue.Enqueue(FinishUpdating);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				actionQueue.Enqueue(FinishUpdating);
			}
			finally
			{
				Monitor.Exit(statusRetriveLock);
			}
		}

		private static void UpdateStatusTreeThreaded(object statusObj)
		{
			Monitor.Enter(statusTreeLock);
			try
			{
				GitRepoStatus status = (GitRepoStatus) statusObj;
				statusTree = new StatusTreeClass(status);
				actionQueue.Enqueue(RepaintProjectWidnow);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
			finally
			{
				Monitor.Exit(statusTreeLock);
				actionQueue.Enqueue(FinishUpdating);
			}
		}

		private static void StartUpdating(string[] paths)
		{
			isUpdating = true;
			updatingFiles.Clear();
			if(paths != null)
				updatingFiles.AddRange(paths);
			GitCallbacks.IssueUpdateRepositoryStart();
		}

		private static void FinishUpdating()
		{
			isUpdating = false;
			updatingFiles.Clear();
			GitCallbacks.IssueUpdateRepositoryFinish();
		}

		internal static bool IsFileUpdating(string path)
		{
			if (isUpdating)
			{
				if (updatingFiles.Count <= 0) return true;
				return updatingFiles.Contains(path);
			}
			return false;
		}

		public static Texture2D GetGitStatusIcon()
		{
			if (!IsValidRepo) return EditorGUIUtility.FindTexture("CollabNew");
			if (Repository == null) return EditorGUIUtility.FindTexture("Collab");
			if (isUpdating) return EditorGUIUtility.FindTexture("WaitSpin00");
			if (Repository.Index.Conflicts.Any()) return EditorGUIUtility.FindTexture("CollabConflict");
			int? behindBy = Repository.Head.TrackingDetails.BehindBy;
			int? aheadBy = Repository.Head.TrackingDetails.AheadBy;
			if (behindBy.GetValueOrDefault(0) > 0)
			{
				return EditorGUIUtility.FindTexture("CollabPull");
			}
			if (aheadBy.GetValueOrDefault(0) > 0)
			{
				return EditorGUIUtility.FindTexture("CollabPush");
			}
			return EditorGUIUtility.FindTexture("Collab");
		}

		#region Auto Fetching
		private static bool AutoFetchChanges()
		{
			if (repository == null || !IsValidRepo || !Settings.AutoFetch) return false;
			Remote remote = repository.Network.Remotes.FirstOrDefault();
			if (remote == null) return false;
			GitProfilerProxy.BeginSample("Git automatic fetching");
			try
			{
				repository.Network.Fetch(remote, new FetchOptions()
				{
					CredentialsProvider = GitCredentialsManager.FetchChangesAutoCredentialHandler,
					OnTransferProgress = FetchTransferProgressHandler,
					RepositoryOperationStarting = (context) =>
					{
						Debug.Log("Repository Operation Starting");
						return true;
					}
				});
				Debug.LogFormat("Auto Fetch From remote: {0} - ({1}) successful.", remote.Name, remote.Url);
			}
			catch (Exception e)
			{
				Debug.LogErrorFormat("Automatic Fetching from remote: {0} with URL: {1} Failed!", remote.Name, remote.Url);
				Debug.LogException(e);
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
			GitProfilerProxy.EndSample();
			return false;
		}
		#endregion

		#region Helpers

		public static void RepaintProjectWidnow()
		{
			Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");
			var projectWindow = Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
			if (projectWindow != null)
			{
				((EditorWindow)projectWindow).Repaint();
			}
		}

		public static bool IsEmptyFolderMeta(string path)
		{
			if (path.EndsWith(".meta"))
			{
				return IsEmptyFolder(path.Substring(0, path.Length - 5));
			}
			return false;
		}

		public static bool IsEmptyFolder(string path)
		{
			if (Directory.Exists(path))
			{
				return Directory.GetFileSystemEntries(path).Length <= 0;
			}
			return false;
		}

		public static string AssetPathFromMeta(string metaPath)
		{
			if (metaPath.EndsWith(".meta"))
			{
				return metaPath.Substring(0, metaPath.Length - 5);
			}
			return metaPath;
		}

		public static string MetaPathFromAsset(string assetPath)
		{
			return assetPath + ".meta";
		}

		public static bool CanStage(FileStatus fileStatus)
		{
			return fileStatus.IsFlagSet(FileStatus.ModifiedInWorkdir | FileStatus.NewInWorkdir | FileStatus.RenamedInWorkdir | FileStatus.TypeChangeInWorkdir | FileStatus.DeletedFromWorkdir);
		}

		public static bool CanUnstage(FileStatus fileStatus)
		{
			return fileStatus.IsFlagSet(FileStatus.ModifiedInIndex | FileStatus.NewInIndex | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex | FileStatus.DeletedFromIndex);
		}

		public static void ShowDiff(string path, [NotNull] Commit start,[NotNull] Commit end)
		{
			if (GitExternalManager.TakeDiff(path, start, end))
			{
				return;
			}


		}

		public static void ShowDiff(string path)
		{
			if (string.IsNullOrEmpty(path) ||  Repository == null) return;
			if (GitExternalManager.TakeDiff(path))
			{
				return;
			}

			EditorWindow.GetWindow<GitDiffInspector>(true).Init(path);
		}

		public static void ShowDiffPrev(string path)
		{
			if (string.IsNullOrEmpty(path) && Repository != null) return;
			var lastCommit = Repository.Commits.QueryBy(path).Skip(1).FirstOrDefault();
			if(lastCommit == null) return;
			if (GitExternalManager.TakeDiff(path, lastCommit.Commit))
			{
				return;
			}

			EditorWindow.GetWindow<GitDiffInspector>(true).Init(path,lastCommit.Commit);
		}

		public static void ShowBlameWizard(string path)
		{
			if (!string.IsNullOrEmpty(path))
			{
				if (GitExternalManager.TakeBlame(path))
				{
					return;
				}

				var blameWizard = EditorWindow.GetWindow<GitBlameWizard>(true);
				blameWizard.SetBlamePath(path);
			}
		}

		public static bool CanBlame(FileStatus fileStatus)
		{
			return fileStatus.AreNotSet(FileStatus.NewInIndex, FileStatus.Ignored,FileStatus.NewInWorkdir);
		}

		public static bool CanBlame(string path)
		{
			return repository.Head[path] != null;
		}
		#endregion

		#region Enumeration helpers

		public static IEnumerable<string> GetPathWithMeta(string path)
		{
			if (path.EndsWith(".meta"))
			{
				if (Path.HasExtension(path)) yield return path;
				string assetPath = AssetPathFromMeta(path);
				if (!string.IsNullOrEmpty(assetPath))
				{
					yield return assetPath;
				}
			}
			else
			{
				if (Path.HasExtension(path)) yield return path;
				string metaPath = MetaPathFromAsset(path);
				if (!string.IsNullOrEmpty(metaPath))
				{
					yield return metaPath;
				}
			}
		}

		public static IEnumerable<string> GetPathsWithMeta(IEnumerable<string> paths)
		{
			return paths.SelectMany(p => GetPathWithMeta(p));
		}
		#endregion

		#region Progress Handlers
		public static bool FetchTransferProgressHandler(TransferProgress progress)
		{
			float percent = (float)progress.ReceivedObjects / progress.TotalObjects;
			bool cancel = EditorUtility.DisplayCancelableProgressBar("Transferring", string.Format("Transferring: Received total of: {0} bytes. {1}%", progress.ReceivedBytes, (percent * 100).ToString("###")), percent);
			if (progress.TotalObjects == progress.ReceivedObjects)
			{
#if UNITY_EDITOR
				Debug.Log("Transfer Complete. Received a total of " + progress.IndexedObjects + " objects");
#endif
			}
			//true to continue
			return !cancel;
		}
		#endregion

		public static void DisablePostprocessing()
		{
			EditorPrefs.SetBool("UniGit_DisablePostprocess",true);
		}

		public static void EnablePostprocessing()
		{
			EditorPrefs.SetBool("UniGit_DisablePostprocess", false);
		}

		#region Getters and Setters

		public static bool IsUpdating
		{
			get { return isUpdating; }
		}

		public static Signature Signature
		{
			get { return new Signature(Repository.Config.GetValueOrDefault<string>("user.name"), Repository.Config.GetValueOrDefault<string>("user.email"),DateTimeOffset.Now);}
		}

		public static bool IsValidRepo
		{
			get { return Repository.IsValid(RepoPath); }
		}

		public static Repository Repository
		{
			get { return repository; }
		}

		public static StatusTreeClass StatusTree
		{
			get { return statusTree; }
		}

		[Obsolete("Use GitCredentialsManager.GitCredentials instead")]
		public static GitCredentialsJson GitCredentials
		{
			get { return GitCredentialsManager.GitCredentials; }
			internal set { GitCredentialsManager.GitCredentials = value; }
		}

		public static GitSettingsJson Settings
		{
			get { return gitSettings; }
		}

		public static string GitFolderPath
		{
			get { return gitPathCached; }
		}

		public static string SettingsFilePath
		{
			get { return Path.Combine(gitPathCached,"UniGit/Settings.json"); }
		}

		public static GitRepoStatus LastStatus
		{
			get { return status; }
		}

		public static Queue<Action> ActionQueue
		{
			get { return actionQueue; }
		}

		#endregion

		#region Status Tree
		public class StatusTreeClass
		{
			private Dictionary<string, StatusTreeEntry> entries = new Dictionary<string, StatusTreeEntry>();
			private string currentPath;
			private string[] currentPathArray;
			private FileStatus currentStatus;

			public StatusTreeClass(IEnumerable<GitStatusEntry> status)
			{
				Build(status);
			}

			private void Build(IEnumerable<GitStatusEntry> status)
			{
				foreach (var entry in status)
				{
					currentPath = entry.Path;
					currentPathArray = entry.Path.Split('\\');
					currentStatus = !Settings.ShowEmptyFolders && IsEmptyFolderMeta(currentPath) ? FileStatus.Ignored : entry.Status;
					AddRecursive(0, entries);
				}
			}

			private void AddRecursive(int entryNameIndex, Dictionary<string, StatusTreeEntry> entries)
			{
				StatusTreeEntry entry;
				string pathChunk = currentPathArray[entryNameIndex].Replace(".meta", "");

				//should a state change be marked at this level (inverse depth)
				//bool markState = Settings.ProjectStatusOverlayDepth < 0 || (Mathf.Abs(currentPathArray.Length - entryNameIndex)) <= Math.Max(1, Settings.ProjectStatusOverlayDepth);
				//markState = true;
				if (entries.TryGetValue(pathChunk, out entry))
				{
					entry.State = entry.State.SetFlags(currentStatus, true);
				}
				else
				{
					entry = new StatusTreeEntry(entryNameIndex);
					entry.State = entry.State.SetFlags(currentStatus);
					entries.Add(pathChunk, entry);
				}
				//check if it's at a allowed depth for status forcing on folders
				if (currentPathArray.Length - entryNameIndex < (Settings.ProjectStatusOverlayDepth+1))
				{
					entry.forceStatus = true;
				}
				if (entryNameIndex < currentPathArray.Length - 1)
				{
					AddRecursive(entryNameIndex + 1, entry.SubEntiEntries);
				}
			}

			public StatusTreeEntry GetStatus(string path)
			{
				StatusTreeEntry entry;
				GetStatusRecursive(0, path.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries), entries, out entry);
				return entry;
			}

			private void GetStatusRecursive(int entryNameIndex, string[] path, Dictionary<string, StatusTreeEntry> entries, out StatusTreeEntry entry)
			{
				if (path.Length <= 0)
				{
					entry = null;
					return;
				}
				StatusTreeEntry entryTmp;
				if (entries.TryGetValue(path[entryNameIndex], out entryTmp))
				{
					if (entryNameIndex < path.Length - 1)
					{
						GetStatusRecursive(entryNameIndex + 1, path, entryTmp.SubEntiEntries, out entry);
						return;
					}
					entry = entryTmp;
					return;
				}

				entry = null;
			}
		}

		public class StatusTreeEntry
		{
			private Dictionary<string, StatusTreeEntry> subEntiEntries = new Dictionary<string, StatusTreeEntry>();
			internal bool forceStatus;
			private int depth;
			public FileStatus State { get; set; }

			public StatusTreeEntry(int depth)
			{
				this.depth = depth;
			}

			public int Depth
			{
				get { return depth; }
			}

			public bool ForceStatus
			{
				get { return forceStatus; }
			}

			public Dictionary<string, StatusTreeEntry> SubEntiEntries
			{
				get { return subEntiEntries; }
				set { subEntiEntries = value; }
			}
		}
		#endregion
	}
}