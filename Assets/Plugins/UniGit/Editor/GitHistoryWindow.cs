﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using JetBrains.Annotations;
using LibGit2Sharp;
using UniGit.Status;
using UniGit.Utils;
using UnityEditor;
using UnityEngine;

namespace UniGit
{
	public class GitHistoryWindow : GitUpdatableWindow, IHasCustomMenu
	{
		private static readonly Color headColor = new Color32(72, 123, 207, 255);
		private static readonly Color remoteColor = new Color32(234, 141, 43, 255);
		private const int CommitsPerExpand = 8;
		private const int MaxFirstCommitCount = 16;

		private Rect toolbarRect { get { return new Rect(0,0,position.width, EditorGUIUtility.singleLineHeight);} }
		private Rect scorllRect { get { return new Rect(0,toolbarRect.height+2,position.width,position.height);} }

		private List<string> lodingProfilePicturesToRemove; 
		private Dictionary<string, WWW> loadingProfilePictures;
		private Dictionary<string, Texture2D> cachedProfilePicturesDictionary;
		[SerializeField] private List<ProfilePicture> serializedProfilePictures;
		private static Styles styles;
		private BranchInfo selectedBranch;
		private BranchInfo[] cachedBranches = new BranchInfo[0];
		private CommitInfo[] cachedCommits = new CommitInfo[0];
		private Rect[] commitRects;
		private Rect historyScrollContentsRect;
		private Rect warningBoxRect;
		private bool hasConflicts;
		private Queue<KeyValuePair<Rect, PopupWindowContent>> popupsQueue = new Queue<KeyValuePair<Rect, PopupWindowContent>>();
		[SerializeField] private Vector2 historyScroll;
		[SerializeField] private string selectedBranchName;
		[SerializeField] private int maxCommitsCount;
		private object commitCachesLock = new object();
		private GitAsyncOperation loadingCommits;

		public class Styles
		{
			public GUIStyle historyKnobNormal;
			public GUIStyle historyKnobHead;
			public GUIStyle historyKnobRemote;
			public GUIStyle historyKnobOther;
			public GUIStyle headCommitTag;
			public GUIStyle remoteCommitTag;
			public GUIStyle otherCommitTag;
			public GUIStyle commitLineHead;
			public GUIStyle commitLineRemote;
			public GUIStyle historyHelpBox;
			public GUIStyle historyHelpBoxLabel;
			public GUIStyle commitMessage;
			public GUIStyle avatar;
			public GUIStyle avatarName;
		}

		[MenuItem("Window/GIT History")]
		public static void CreateEditor()
		{
			GetWindow(true);
		}

		public static GitHistoryWindow GetWindow(bool focus)
		{
			return GetWindow<GitHistoryWindow>("Git History", focus);
		}

		protected override void OnEnable()
		{
			base.OnEnable();
			if (maxCommitsCount <= 0)
			{
				maxCommitsCount = MaxFirstCommitCount;
			}
		}

		private void CreateStyles()
		{
			if (styles == null)
			{
				GitProfilerProxy.BeginSample("Git History Window Style Creation",this);
				Texture2D blueTexture = new Texture2D(1,1) {hideFlags = HideFlags.HideAndDontSave};
				blueTexture.SetPixel(0,0, headColor);
				blueTexture.Apply();

				Texture2D orangeTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
				orangeTexture.SetPixel(0, 0, remoteColor);
				orangeTexture.Apply();

				styles = new Styles();
				styles.historyKnobNormal = new GUIStyle("sv_iconselector_labelselection");
				styles.historyKnobNormal.border = new RectOffset(6, 6, 6, 6);
				styles.historyKnobNormal.margin = new RectOffset(0, 0, 0, 0);
				styles.historyKnobNormal.fixedHeight = 0;
				styles.historyKnobHead = new GUIStyle() {border = new RectOffset(6,6,6,6),fixedHeight = 0,normal = new GUIStyleState() {background = EditorGUIUtility.FindTexture("sv_label_1") } };
				styles.historyKnobRemote = new GUIStyle() { border = new RectOffset(6, 6, 6, 6), fixedHeight = 0, normal = new GUIStyleState() { background = EditorGUIUtility.FindTexture("sv_label_5") } };
				styles.historyKnobOther = new GUIStyle() { border = new RectOffset(6, 6, 6, 6), fixedHeight = 0, normal = new GUIStyleState() { background = EditorGUIUtility.FindTexture("sv_label_2") } };
				styles.headCommitTag = new GUIStyle("AssetLabel");
				styles.headCommitTag.normal.background = EditorGUIUtility.FindTexture("sv_label_1");
				styles.remoteCommitTag = new GUIStyle("AssetLabel");
				styles.remoteCommitTag.normal.background = EditorGUIUtility.FindTexture("sv_label_5");
				styles.otherCommitTag = new GUIStyle("AssetLabel");
				styles.otherCommitTag.normal.background = ((GUIStyle)"sv_iconselector_labelselection").normal.background;
				styles.commitLineHead = new GUIStyle() {normal = new GUIStyleState() { background = blueTexture } };
				styles.commitLineRemote = new GUIStyle() { normal = new GUIStyleState() { background = orangeTexture } };
				styles.historyHelpBox = new GUIStyle(EditorStyles.helpBox) {richText = true,padding = new RectOffset(8,8,8,8),alignment = TextAnchor.MiddleLeft,contentOffset = new Vector2(24,-2)};
				styles.historyHelpBoxLabel = new GUIStyle("CN EntryWarn");
				styles.commitMessage = new GUIStyle("TL SelectionButton") {alignment = TextAnchor.UpperLeft,padding = new RectOffset(6,4,4,4),clipping = TextClipping.Clip};
				styles.avatar = new GUIStyle("ShurikenEffectBg") {contentOffset = Vector3.zero, alignment = TextAnchor.MiddleCenter,clipping = TextClipping.Clip,imagePosition = ImagePosition.ImageOnly};
				styles.avatarName = new GUIStyle("ShurikenEffectBg") {contentOffset = Vector3.zero, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip, imagePosition = ImagePosition.TextOnly, fontSize = 28, fontStyle = FontStyle.Bold, normal = {textColor = Color.white}};
				GitProfilerProxy.EndSample();
			}
		}

		protected override void OnGitUpdate(GitRepoStatus status,string[] path)
		{
			Repaint();
			StartUpdateChaches(status);
		}

		private void StartUpdateChaches(GitRepoStatus status)
		{
			loadingCommits = GitAsyncManager.QueueWorker(UpdateChachesThreaded, status, "Loading Commits");
		}

		private void UpdateChachesThreaded(GitRepoStatus status)
		{
			Monitor.Enter(commitCachesLock);
			try
			{
				//update all branches
				cachedBranches = GitManager.Repository.Branches.Select(b => new BranchInfo(b)).ToArray();

				//update selected branch
				SetSelectedBranch(selectedBranchName);

				int commitCount = 0;
				if (selectedBranch != null)
				{
					//update commits and limit them depending on settings
					var loadedBranch = selectedBranch.LoadBranch();
					if (loadedBranch != null && loadedBranch.Commits != null)
					{
						IEnumerable<Commit> commits = maxCommitsCount >= 0 ? loadedBranch.Commits.Take(maxCommitsCount) : loadedBranch.Commits;
						if (commits != null)
						{
							cachedCommits = commits.Take(maxCommitsCount).Select(c => new CommitInfo(c, cachedBranches.Where(b => b.Tip.Id == c.Id).ToArray())).ToArray();
							commitCount = cachedCommits.Length;
						}
					}
				}

				commitRects = new Rect[commitCount];
				hasConflicts = status.Any(s => s.Status == FileStatus.Conflicted);
				GitManager.ActionQueue.Enqueue(UpdateGitStatusIcon);
				GitManager.ActionQueue.Enqueue(Repaint);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
			finally
			{
				Monitor.Exit(commitCachesLock);
			}
		}

		private void UpdateGitStatusIcon()
		{
			titleContent.image = GitManager.GetGitStatusIcon();
		}

		private void SetSelectedBranch(string canonicalName)
		{
			selectedBranchName = canonicalName;
			var tmpBranch = GitManager.Repository.Branches.FirstOrDefault(b => b.CanonicalName == canonicalName);
			if (tmpBranch != null)
			{
				selectedBranch = new BranchInfo(tmpBranch);

			}
			if (selectedBranch == null)
			{
				selectedBranch = new BranchInfo(GitManager.Repository.Head);
				selectedBranchName = selectedBranch.CanonicalName;
			}
		}

		protected override void OnInitialize()
		{
			lodingProfilePicturesToRemove = new List<string>();
			cachedProfilePicturesDictionary = new Dictionary<string, Texture2D>();
			loadingProfilePictures = new Dictionary<string, WWW>();
			if (serializedProfilePictures != null)
			{
				foreach (var picture in serializedProfilePictures)
				{
					cachedProfilePicturesDictionary.Add(picture.email, picture.texture);
				}
			}
			else
			{
				serializedProfilePictures = new List<ProfilePicture>();
			}
		}

		protected override void OnEditorUpdate()
		{
			if (loadingProfilePictures == null) loadingProfilePictures = new Dictionary<string, WWW>();
			if (cachedProfilePicturesDictionary == null) cachedProfilePicturesDictionary = new Dictionary<string, Texture2D>();
			if(lodingProfilePicturesToRemove == null) lodingProfilePicturesToRemove = new List<string>();

			if (loadingProfilePictures.Count > 0)
			{
				foreach (var profilePicture in loadingProfilePictures)
				{
					if (profilePicture.Value.isDone)
					{
						cachedProfilePicturesDictionary.Add(profilePicture.Key,profilePicture.Value.texture);
						serializedProfilePictures.RemoveAll(p => p.email == profilePicture.Key);
						serializedProfilePictures.Add(new ProfilePicture(profilePicture.Value.texture, profilePicture.Key));
						lodingProfilePicturesToRemove.Add(profilePicture.Key);
						profilePicture.Value.Dispose();
						Repaint();
					}
				}

				if (lodingProfilePicturesToRemove.Count > 0)
				{
					foreach (var key in lodingProfilePicturesToRemove)
					{
						loadingProfilePictures.Remove(key);
					}
					lodingProfilePicturesToRemove.Clear();
				}
			}
		}

		protected override void OnRepositoryLoad(Repository repository)
		{
			Repaint();
		}

		protected override void OnFocus()
		{
			base.OnFocus();
			GUI.FocusControl(null);
		}

		[UsedImplicitly]
		private void OnUnfocus()
		{
			GUI.FocusControl(null);
		}

		[UsedImplicitly]
		private void OnDestory()
		{
			/*foreach (var profilePicture in cachedProfilePicturesDictionary)
			{
				profilePicture.Value.Dispose();
			}*/
		}

		private const float helpBoxHeight = 38;
		private readonly float commitSpacing = EditorGUIUtility.singleLineHeight / 2;

		[UsedImplicitly]
		private void OnGUI()
		{
			CreateStyles();

			if (!GitManager.IsValidRepo)
			{
				InvalidRepoGUI();
				return;
			}

			if(GitManager.Repository == null || selectedBranch == null) return;
			RepositoryInformation repoInformation = GitManager.Repository.Info;
			DoToolbar(toolbarRect, repoInformation);
			EditorGUILayout.Space();

			DoHistoryScrollRect(scorllRect, repoInformation);

			if (popupsQueue.Count > 0)
			{
				var content = popupsQueue.Dequeue();
				PopupWindow.Show(content.Key, content.Value);
			}
		}

		private void DoToolbar(Rect rect, RepositoryInformation info)
		{
			Branch branch = selectedBranch.LoadBranch();
			if (branch == null)
			{
				EditorGUILayout.HelpBox(string.Format("Invalid Branch: '{0}'", selectedBranch.CanonicalName),MessageType.Warning,true);
				return;
			}

			GitGUI.StartEnable();
			GitProfilerProxy.BeginSample("Git History Window Toolbar GUI",this);
			GUI.Box(rect, GUIContent.none, "Toolbar");
			Rect btRect = new Rect(rect.x, rect.y, 64, rect.height);
			GUIContent pushButtonContent = GitGUI.GetTempContent(EditorGUIUtility.FindTexture("CollabPush"),"Push", "Push local changes to a remote repository.");
			if (info.CurrentOperation == CurrentOperation.Merge)
			{
				GUI.enabled = false;
				pushButtonContent.tooltip = "Do a Merge commit before pushing.";
			}
			else if (hasConflicts)
			{
				GUI.enabled = false;
				pushButtonContent.tooltip = "Resolve conflicts before pushing.";
			}
			else if (selectedBranch == null)
			{
				GUI.enabled = false;
				pushButtonContent.tooltip = "No Selected branch. Create a new branch or create atleast one commit.";
			}
			if (GUI.Button(btRect, pushButtonContent, "toolbarbutton"))
			{
				GoToPush();
			}
			btRect = new Rect(btRect.x + 64, btRect.y, 64, btRect.height);
			GUI.enabled = !hasConflicts;
			if (GUI.Button(btRect, GitGUI.IconContent("CollabPull", "Pull", hasConflicts ? "Must resolve conflicts before pulling" : "Pull changes from remote repository by fetching them and then merging them. This is the same as calling Fetch then Merge."), "toolbarbutton"))
			{
				GoToPull();
			}
			btRect = new Rect(btRect.x + 70, btRect.y, 64, btRect.height);
			GUIContent fetchContent = new GUIContent("Fetch", GitOverlay.icons.fetch.image, "Get changes from remote repository but do not merge them.");
			if (branch.Remote == null)
			{
				fetchContent.tooltip = "Branch does not have a remote.";
				GUI.enabled = false;
			}
			if (GUI.Button(btRect, fetchContent, "toolbarbutton"))
			{
				GoToFetch();
			}
			GUI.enabled = true;
			btRect = new Rect(btRect.x + 64, btRect.y, 64, btRect.height);
			if (GUI.Button(btRect, GitGUI.GetTempContent(GitOverlay.icons.merge.image, "Merge", hasConflicts ? "Must Resolve conflict before merging" : "Merge fetched changes from remote repository. Changes from the latest fetch will be merged."), "toolbarbutton"))
			{
				GoToMerge();
			}
			GUI.enabled = GitManager.IsValidRepo;
			btRect = new Rect(btRect.x + 64,btRect.y,64,btRect.height);
			if (GUI.Button(btRect, GitGUI.GetTempContent(GitOverlay.icons.stashIcon.image,"Stash"), "toolbarbutton"))
			{
				PopupWindow.Show(btRect,new GitStashWindow());
			}
			GUI.enabled = true;

			GUIContent branchNameContent = GitGUI.GetTempContent(string.IsNullOrEmpty(selectedBranchName) ? "Branch" : selectedBranch.FriendlyName);
			if (selectedBranch.IsRemote)
				branchNameContent.image = GitGUI.IconContentTex("ToolHandleGlobal");
			else if (!selectedBranch.IsCurrentRepositoryHead)
				branchNameContent.image = GitGUI.IconContentTex("IN LockButton on");

			float branchNameWidth = ((GUIStyle) "ToolbarDropDown").CalcSize(branchNameContent).x;
			btRect = new Rect(rect.x + rect.width - branchNameWidth, btRect.y, branchNameWidth, btRect.height);
			if (GUI.Button(btRect, branchNameContent, "ToolbarDropDown"))
			{
				GenericMenu selectBranchMenu = new GenericMenu();
				foreach (var cachedBranch in cachedBranches)
				{
					selectBranchMenu.AddItem(new GUIContent(cachedBranch.FriendlyName), selectedBranchName == cachedBranch.CanonicalName, (b) =>
					{
						SetSelectedBranch((string)b);
						StartUpdateChaches(GitManager.LastStatus);
					}, cachedBranch.CanonicalName);
				}
				selectBranchMenu.ShowAsContext();
			}
			GitGUI.EndEnable();
			btRect = new Rect(btRect.x - 64, btRect.y, 64, btRect.height);
			GitGUI.StartEnable(GitManager.Settings.ExternalsType.HasFlag(GitSettings.ExternalsTypeEnum.Switch) || (!selectedBranch.IsRemote && !selectedBranch.IsCurrentRepositoryHead));
			if (GUI.Button(btRect, GitGUI.GetTempContent(GitOverlay.icons.checkout.image, "Switch", selectedBranch.IsRemote ? "Cannot switch to remote branches." : selectedBranch.IsCurrentRepositoryHead ? "This branch is the active one" : "Switch to another branch"), "toolbarbutton"))
			{
				if (GitExternalManager.TakeSwitch())
				{
					AssetDatabase.Refresh();
					GitManager.MarkDirty();
				}
				else
				{
					PopupWindow.Show(btRect,new GitCheckoutWindowPopup(selectedBranch.LoadBranch()));
				}
			}
			GitGUI.EndEnable();
			btRect = new Rect(btRect.x - 21, btRect.y+1, 21, btRect.height);
			if (GUI.Button(btRect, GitGUI.IconContent("_Help"), "IconButton"))
			{
				GoToHelp();
			}
			GitProfilerProxy.EndSample();
		}

		private void GoToMerge()
		{
			if (GitExternalManager.TakeMerge())
			{
				GitManager.MarkDirty();
			}
			else
			{
				ScriptableWizard.DisplayWizard<GitMergeWizard>("Merge", "Merge");
			}
		}

		private void GoToFetch()
		{
			var branch = selectedBranch.LoadBranch();
			if (GitExternalManager.TakeFetch(branch.Remote.Name))
			{
				GitManager.MarkDirty();
			}
			else
			{
				ScriptableWizard.DisplayWizard<GitFetchWizard>("Fetch", "Fetch").Init(branch);
			}
		}

		private void GoToPull()
		{
			if (GitExternalManager.TakePull())
			{
				AssetDatabase.Refresh();
				GitManager.MarkDirty();
			}
			else
			{
				ScriptableWizard.DisplayWizard<GitPullWizard>("Pull", "Pull").Init(selectedBranch.LoadBranch());
			}
		}

		private void GoToPush()
		{
			if (GitExternalManager.TakePush())
			{
				GitManager.MarkDirty();
			}
			else
			{
				ScriptableWizard.DisplayWizard<GitPushWizard>("Push", "Push").Init(selectedBranch.LoadBranch());
			}
		}

		private void GoToHelp()
		{
			Application.OpenURL("https://github.com/simeonradivoev/UniGit/wiki/Commit-History");
		}

		private void DoHistoryScrollRect(Rect rect, RepositoryInformation info)
		{
			if (loadingCommits != null && !loadingCommits.IsDone)
			{
				Repaint();
				GitGUI.DrawLoading(rect, new GUIContent("Loading Commit History"));
				return;
			}

			Event current = Event.current;

			GUI.Box(new Rect(14, rect.y + 2, 2, rect.height), GUIContent.none, "AppToolbar");

			//behind,ahead and merge checking

			bool displayWarnningBox = DoWarningBoxValidate(info,selectedBranch);

			//commit layout
			if (current.type == EventType.Layout)
			{
				GitProfilerProxy.BeginSample("Git History Window Scroll Rect GUI Layout", this);
				Rect lastCommitRect = new Rect(32, commitSpacing, Mathf.Max(rect.width - 24, 512) - 32, 0);

				if (displayWarnningBox)
				{
					warningBoxRect = new Rect(lastCommitRect.x, lastCommitRect.y, lastCommitRect.width, helpBoxHeight);
					lastCommitRect.y += helpBoxHeight + commitSpacing;
				}

				for (int i = 0; i < cachedCommits.Length; i++)
				{
					lastCommitRect = LayoutCommit(lastCommitRect, cachedCommits[i]);
					if (i < commitRects.Length)
					{
						commitRects[i] = lastCommitRect;
					}
				}

				historyScrollContentsRect = new Rect(0, 0, lastCommitRect.width + 32, lastCommitRect.y + lastCommitRect.height + commitSpacing*2);
				historyScrollContentsRect.height += EditorGUIUtility.singleLineHeight*3;
				GitProfilerProxy.EndSample();
			}
			else
			{
				GitProfilerProxy.BeginSample("Git History Window Scroll Rect GUI Other", this);
				historyScroll = GUI.BeginScrollView(rect, historyScroll, historyScrollContentsRect);

				if (displayWarnningBox)
				{
					DoWarningBox(warningBoxRect, info,selectedBranch);
				}

				for (int i = 0; i < cachedCommits.Length; i++)
				{
					if (i < commitRects.Length)
					{
						DoCommit(commitRects[i], rect, cachedCommits[i]);
					}
				}

				Rect commitsCountRect = new Rect(32, historyScrollContentsRect.height - EditorGUIUtility.singleLineHeight * 4, historyScrollContentsRect.width - 64, EditorGUIUtility.singleLineHeight);

				GUI.Label(commitsCountRect,GitGUI.GetTempContent(cachedCommits.Length + " / " + maxCommitsCount),EditorStyles.centeredGreyMiniLabel);

				Rect resetRect = new Rect(historyScrollContentsRect.width / 2, historyScrollContentsRect.height - EditorGUIUtility.singleLineHeight * 3, 64, EditorGUIUtility.singleLineHeight);
				Rect loadMoreRect = new Rect(historyScrollContentsRect.width / 2 - 64, historyScrollContentsRect.height - EditorGUIUtility.singleLineHeight * 3, 64, EditorGUIUtility.singleLineHeight);
				if (GUI.Button(loadMoreRect, GitGUI.IconContent("ol plus", "More","Show more commits."), "ButtonLeft"))
				{
					maxCommitsCount += CommitsPerExpand;
					StartUpdateChaches(GitManager.LastStatus);
				}
				GitGUI.StartEnable(maxCommitsCount != MaxFirstCommitCount);
				if (GUI.Button(resetRect, GitGUI.GetTempContent("Reset","Reset the number of commits show."), "ButtonRight"))
				{
					if (MaxFirstCommitCount < maxCommitsCount)
					{
						maxCommitsCount = MaxFirstCommitCount;
						Array.Resize(ref cachedCommits, maxCommitsCount);
					}
					else
					{
						maxCommitsCount = MaxFirstCommitCount;
						StartUpdateChaches(GitManager.LastStatus);
					}
				}
				GitGUI.EndEnable();
				GUI.EndScrollView();
				GitProfilerProxy.EndSample();
			}
			
		}

		private Rect LayoutCommit(Rect lastCommitRect, CommitInfo commit)
		{
			bool isHeadOrRemote = commit.IsRemote || commit.Branches != null;
			float commitHeight = 7 * EditorGUIUtility.singleLineHeight;
			if (isHeadOrRemote) commitHeight += EditorGUIUtility.singleLineHeight;
			Rect commitRect = new Rect(lastCommitRect.x, lastCommitRect.y + lastCommitRect.height + commitSpacing, lastCommitRect.width, commitHeight);
			return commitRect;
		}

		private void DoCommit(Rect rect,Rect scrollRect,CommitInfo commit)
		{
			GitProfilerProxy.BeginSample("Git History Window Commit GUI",this);
			Event current = Event.current;

			if (rect.y > scrollRect.height + historyScroll.y || rect.y + scrollRect.height < historyScroll.y)
			{
				return;
			}

			BranchInfo[] branches = commit.Branches;
			bool isHead = commit.IsHead;
			bool isRemote = commit.IsRemote;

			Color branchColor = Color.white;
			if (branches != null)
			{
				foreach (var branch in branches)
				{
					if (branch.IsRemote)
					{
						branchColor = remoteColor;
						break;
					}
					if (branch.IsCurrentRepositoryHead)
					{
						branchColor = headColor;
						break;
					}
					UnityEngine.Random.InitState(branch.CanonicalName.GetHashCode());
					branchColor = UnityEngine.Random.ColorHSV(0, 1, 0, 1);
				}
			}

			GUI.backgroundColor = new Color(1,1,1,0.4f);
			GUI.Box(new Rect(24, rect.y + 5, 16, 16), GUIContent.none, "AC LeftArrow");
			GUI.backgroundColor = branchColor;
			GUI.Box(new Rect(9, rect.y + 6, 12, 12), GUIContent.none, styles.historyKnobNormal);
			GUI.backgroundColor = Color.white;

			float y = 8;
			float x = 12;
			if (isHead)
			{
				//GUI.Box(new Rect(commitRect.x + 4, commitRect.y, commitRect.width - 8, commitRect.height - 8), GUIContent.none, "TL SelectionButton PreDropGlow");
			}
			GUI.Box(rect, GUIContent.none, "RegionBg");
			if (isHead || isRemote)
			{
				GUI.Box(new Rect(rect.x + 4, rect.y, rect.width - 8, 5), GUIContent.none, isHead ? styles.commitLineHead : styles.commitLineRemote);
				y += 4;
			}

			if (GitManager.Settings.UseGavatar && Application.internetReachability != NetworkReachability.NotReachable)
			{
				Texture2D avatar = GetProfilePixture(commit.Committer.Email);
				if (avatar != null)
				{
					GUI.Box(new Rect(rect.x + x, rect.y + y, 32, 32), GitGUI.GetTempContent(avatar), styles.avatar);
				}
				else
				{
					GUI.Box(new Rect(rect.x + x, rect.y + y, 32, 32), GitOverlay.icons.loadingIconSmall, styles.avatar);
				}
			}
			else
			{
				UnityEngine.Random.InitState(commit.Committer.Name.GetHashCode());
				GUI.contentColor = UnityEngine.Random.ColorHSV(0, 1, 0.6f, 0.6f, 0.8f, 1, 1, 1);
				GUI.Box(new Rect(rect.x + x, rect.y + y, 32, 32), GitGUI.GetTempContent(commit.Committer.Name.Substring(0,1).ToUpper()), styles.avatarName);
				GUI.contentColor = Color.white;
			}
			
			
			//if (avatar != null)
			//{
				//GUI.DrawTexture(new Rect(rect.x + x, rect.y + y, 32, 32), avatar);
			//}
			x += 38;
			EditorGUI.LabelField(new Rect(rect.x + x, rect.y + y, rect.width - x, EditorGUIUtility.singleLineHeight), GitGUI.GetTempContent(commit.Committer.Name), EditorStyles.boldLabel);
			y += 16;
			EditorGUI.LabelField(new Rect(rect.x + x, rect.y + y, rect.width - x, EditorGUIUtility.singleLineHeight), GitGUI.GetTempContent(FormatRemainningTime(commit.Committer.When.UtcDateTime)));
			y += EditorGUIUtility.singleLineHeight + 3;
			int firstNewLineIndex = commit.Message.IndexOf(Environment.NewLine);
			EditorGUI.LabelField(new Rect(rect.x + x, rect.y + y, rect.width - x - 10, EditorGUIUtility.singleLineHeight + 4), GitGUI.GetTempContent(firstNewLineIndex > 0 ? commit.Message.Substring(0, firstNewLineIndex) : commit.Message), styles.commitMessage);
			y += 8;
			if (branches != null)
			{
				if (branches.Length > 0)
				{
					y += EditorGUIUtility.singleLineHeight;
				}
				foreach (var branch in branches)
				{
					GUIStyle style = styles.otherCommitTag;
					if (branch.IsRemote)
					{
						GUI.backgroundColor = remoteColor;
					}
					else if (branch.IsCurrentRepositoryHead)
					{
						GUI.backgroundColor = headColor;
					}
					else
					{
						UnityEngine.Random.InitState(branch.CanonicalName.GetHashCode());
						GUI.backgroundColor = UnityEngine.Random.ColorHSV(0, 1, 0, 1);
					}
					GUIContent labelContent = GitGUI.GetTempContent(branch.FriendlyName, branch.CanonicalName);
					float labelWidth = style.CalcSize(labelContent).x;
					Rect branchIconRect = new Rect(rect.x + x, rect.y + y, labelWidth, EditorGUIUtility.singleLineHeight);
					GUI.Label(branchIconRect, labelContent, style);
					x += labelWidth + 4;
					GUI.backgroundColor = Color.white;

					if (Event.current.type == EventType.ContextClick && branchIconRect.Contains(Event.current.mousePosition))
					{
						GenericMenu branchContextMenu = new GenericMenu();
						BranchInfo b = branch;
						branchContextMenu.AddItem(new GUIContent("View branch"),false,()=> { ViewBranchCallback(b); });
						if(!b.IsRemote && !b.IsCurrentRepositoryHead)
							branchContextMenu.AddItem(new GUIContent("Switch To Branch"), false,()=> { SwitchToBranchCallback(b, new Rect(branchIconRect.x - historyScroll.x, branchIconRect.y - historyScroll.y, branchIconRect.width, branchIconRect.height)); });
						else
							branchContextMenu.AddDisabledItem(new GUIContent("Switch To Branch"));
						branchContextMenu.ShowAsContext();
					}
				}
			}

			x = 12;
			y += EditorGUIUtility.singleLineHeight * 1.5f;
			GUI.Box(new Rect(rect.x + x, rect.y + y, rect.width - x - x, EditorGUIUtility.singleLineHeight), GUIContent.none, "EyeDropperHorizontalLine");
			y += EditorGUIUtility.singleLineHeight / 3;
			EditorGUI.LabelField(new Rect(rect.x + x, rect.y + y, rect.width - x, EditorGUIUtility.singleLineHeight), GitGUI.GetTempContent(commit.Id.Sha));
			x += GUI.skin.label.CalcSize(GitGUI.GetTempContent(commit.Id.Sha)).x + 8;
			Rect buttonRect = new Rect(rect.x + x, rect.y + y, 64, EditorGUIUtility.singleLineHeight);
			x += 64;
			if (GUI.Button(buttonRect, GitGUI.GetTempContent("Options"), "minibuttonleft"))
			{
				GenericMenu menu = new GenericMenu();

				if (selectedBranch.IsCurrentRepositoryHead && !isHead)
				{
					menu.AddItem(new GUIContent("Reset"), false, () =>
					{
						if (GitExternalManager.TakeReset(GitManager.Repository.Lookup<Commit>(commit.Id)))
						{
							AssetDatabase.Refresh();
							GitManager.MarkDirty();
						}
						else
						{
							popupsQueue.Enqueue(new KeyValuePair<Rect, PopupWindowContent>(buttonRect, new ResetPopupWindow(GitManager.Repository.Lookup<Commit>(commit.Id))));
						}
					});
				}
				else
				{
					menu.AddDisabledItem(new GUIContent("Reset"));
				}
				menu.AddItem(new GUIContent("Branch Out"), false, () =>
				{
					popupsQueue.Enqueue(new KeyValuePair<Rect, PopupWindowContent>(buttonRect, new GitCreateBranchWindow(this, GitManager.Repository.Lookup<Commit>(commit.Id),null)));
				});
				menu.DropDown(buttonRect);
			}
			GUI.enabled = true;
			buttonRect = new Rect(rect.x + x, rect.y + y, 64, EditorGUIUtility.singleLineHeight);
			if (GUI.Button(buttonRect, GitGUI.GetTempContent("Details"), "minibuttonright"))
			{
				PopupWindow.Show(buttonRect, new GitCommitDetailsWindow(GitManager.Repository.Lookup<Commit>(commit.Id)));
			}

			if (rect.Contains(current.mousePosition))
			{
				if (current.type == EventType.ContextClick)
				{
					//GenericMenu commitContexMenu = new GenericMenu();

					//commitContexMenu.ShowAsContext();
					current.Use();
				}
			}
			GitProfilerProxy.EndSample();
		}

		private void ViewBranchCallback(BranchInfo branch)
		{
			SetSelectedBranch(branch.CanonicalName);
			StartUpdateChaches(GitManager.LastStatus);
		}

		private void SwitchToBranchCallback(BranchInfo branch,Rect rect)
		{
			popupsQueue.Enqueue(new KeyValuePair<Rect, PopupWindowContent>(rect,new GitCheckoutWindowPopup(branch.LoadBranch())));
		}

		private void DoWarningBox(Rect rect, RepositoryInformation info, BranchInfo branch)
		{
			int? behindBy = selectedBranch.TrackingDetails.BehindBy;
			GUIContent content = GUIContent.none;
			if (info.CurrentOperation == CurrentOperation.Merge)
			{
				content = GitGUI.GetTempContent(string.Format("Merging with remote branch in progress. You <b>must</b> do a merge commit before pushing."));
			}
			else if (behindBy != null && behindBy.Value > 0)
			{
				content = GitGUI.GetTempContent(string.Format("Branch <b>{0}</b> behind tracked branch <b>{1}</b>", selectedBranch.FriendlyName, selectedBranch.TrackedBranch));
			}
			else if (branch.IsRemote)
			{
				content = GitGUI.GetTempContent("Viewing a remote branch. Showing local history of remote branch.");
			}
			else if (!branch.IsCurrentRepositoryHead)
			{
				content = GitGUI.GetTempContent("Viewing a branch that is not the HEAD.");
			}

			GUI.Box(rect, content, styles.historyHelpBox);
			GUI.Box(rect, GUIContent.none, styles.historyHelpBoxLabel);
		}

		private bool DoWarningBoxValidate(RepositoryInformation info,BranchInfo branchInfo)
		{
			int? behindBy = selectedBranch.TrackingDetails.BehindBy;
			return (behindBy != null && behindBy.Value > 0) | info.CurrentOperation == CurrentOperation.Merge | !branchInfo.IsCurrentRepositoryHead | branchInfo.IsRemote;
		}

		#region Menus

		public void AddItemsToMenu(GenericMenu menu)
		{
			menu.AddItem(new GUIContent("Push"), false, GoToPush);
			menu.AddItem(new GUIContent("Pull"),false,GoToPull);
			menu.AddItem(new GUIContent("Fetch"), false, GoToFetch);
			menu.AddItem(new GUIContent("Merge"), false, GoToMerge);
			menu.AddItem(new GUIContent("Help"),false, GoToHelp);
		}

		#endregion

		#region Helper Methods
		private Texture2D GetProfilePixture(string email)
		{
			Texture2D tex;
			if (cachedProfilePicturesDictionary.TryGetValue(email, out tex))
			{
				if (tex != null)
				{
					return tex;
				}
				cachedProfilePicturesDictionary.Remove(email);
			}

			WWW texWww;
			if (loadingProfilePictures.TryGetValue(email,out texWww))
			{
				if (texWww.isDone)
				{
					tex = texWww.texture;
					cachedProfilePicturesDictionary.Add(email, tex);
					serializedProfilePictures.RemoveAll(p => p.email == email);
					serializedProfilePictures.Add(new ProfilePicture(tex,email));
					loadingProfilePictures.Remove(email);
					texWww.Dispose();
				}
				
				return tex;
			}

			string hash = HashEmailForGravatar(email.Trim());
			WWW loading = new WWW("https://www.gravatar.com/avatar/" + hash + "?s=32");
			loadingProfilePictures.Add(email, loading);
			return null;
		}

		public static string HashEmailForGravatar(string email)
		{
			// Create a new instance of the MD5CryptoServiceProvider object.  
			MD5 md5Hasher = MD5.Create();

			// Convert the input string to a byte array and compute the hash.  
			byte[] data = md5Hasher.ComputeHash(Encoding.Default.GetBytes(email));

			// Create a new Stringbuilder to collect the bytes  
			// and create a string.  
			StringBuilder sBuilder = new StringBuilder();

			// Loop through each byte of the hashed data  
			// and format each one as a hexadecimal string.  
			for (int i = 0; i < data.Length; i++)
			{
				sBuilder.Append(data[i].ToString("x2"));
			}

			return sBuilder.ToString(); // Return the hexadecimal string. 
		}

		private string FormatRemainningTime(DateTime timeOffset)
		{
			const int SECOND = 1;
			const int MINUTE = 60 * SECOND;
			const int HOUR = 60 * MINUTE;
			const int DAY = 24 * HOUR;
			const int MONTH = 30 * DAY;

			var ts = new TimeSpan(DateTime.UtcNow.Ticks - timeOffset.Ticks);
			double delta = Math.Abs(ts.TotalSeconds);

			if (delta < 1 * MINUTE)
				return ts.Seconds == 1 ? "one second ago" : ts.Seconds + " seconds ago";

			if (delta < 2 * MINUTE)
				return "a minute ago";

			if (delta < 45 * MINUTE)
				return ts.Minutes + " minutes ago";

			if (delta < 90 * MINUTE)
				return "an hour ago";

			if (delta < 24 * HOUR)
				return ts.Hours + " hours ago";

			if (delta < 48 * HOUR)
				return "yesterday";

			if (delta < 30 * DAY)
				return ts.Days + " days ago";

			if (delta < 12 * MONTH)
			{
				int months = Convert.ToInt32(Math.Floor((double)ts.Days / 30));
				return months <= 1 ? "one month ago" : months + " months ago";
			}
			else
			{
				int years = Convert.ToInt32(Math.Floor((double)ts.Days / 365));
				return years <= 1 ? "one year ago" : years + " years ago";
			}

		}
		#endregion

		#region Invalid Repo GUI
		internal static void InvalidRepoGUI()
		{
			EditorGUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			GUILayout.Box(GitGUI.GetTempContent("Not a GIT Repository"), "NotificationBackground");
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			//initialization tips
			EditorGUILayout.HelpBox("If you have an existing remote repository and want to clone it, you will have to do so outside of the editor.", MessageType.Info);
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();

			GUILayout.FlexibleSpace();
			EditorGUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			if (GUILayout.Button(GitGUI.GetTempContent("Create"), "LargeButton", GUILayout.Height(32), GUILayout.Width(128)))
			{
				if (EditorUtility.DisplayDialog("Initialize Repository", "Are you sure you want to initialize a Repository for your project", "Yes", "Cancel"))
				{
					GitManager.InitilizeRepository();
					GUIUtility.ExitGUI();
					return;
				}
			}
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();
			EditorGUILayout.Space();
		}
		#endregion

		#region Popup Windows

		private abstract class CommitPopupWindow : PopupWindowContent
		{
			protected Commit commit;

			protected CommitPopupWindow(Commit commit)
			{
				this.commit = commit;
			}
		}

		private class ResetPopupWindow : CommitPopupWindow
		{
			private ResetMode resetMode = ResetMode.Mixed;
			private CheckoutOptions checkoutOptions = new CheckoutOptions();

			public override Vector2 GetWindowSize()
			{
				return new Vector2(256,128);
			}

			public ResetPopupWindow(Commit commit) : base(commit)
			{
				
			}

			public override void OnGUI(Rect rect)
			{
				EditorGUILayout.Space();
				resetMode = (ResetMode)EditorGUILayout.EnumPopup(GitGUI.GetTempContent("Reset Type"), resetMode);
				switch (resetMode)
				{
					case ResetMode.Soft:
						EditorGUILayout.HelpBox("Leave working tree and index untouched",MessageType.Info);
						break;
					case ResetMode.Mixed:
						EditorGUILayout.HelpBox("Leave working tree untouched,reset index (Default)", MessageType.Info);
						break;
					case ResetMode.Hard:
						EditorGUILayout.HelpBox("Reset working tree and index (Will delete all files)",MessageType.Error);
						break;
				}
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("Reset"))
				{
					if (EditorUtility.DisplayDialog("Reset", "Are you sure you want to reset to the selected commit", "Reset", "Cancel"))
					{
						GitProfilerProxy.BeginSample("Git Reset Popup",editorWindow);
						GitManager.Repository.Reset(resetMode,commit, checkoutOptions);
						GitManager.MarkDirty(true);
						editorWindow.Close();
						GitProfilerProxy.EndSample();
						AssetDatabase.Refresh();
					}
				}
				EditorGUILayout.Space();
			}
		}
		#endregion

		[Serializable]
		public struct ProfilePicture
		{
			public Texture2D texture;
			public string email;

			public ProfilePicture(Texture2D texture, string email)
			{
				this.texture = texture;
				this.email = email;
			}
		}

		public class BranchInfo
		{
			public readonly string CanonicalName;
			public readonly Commit Tip;
			public readonly string TrackedBranch;
			public readonly BranchTrackingDetails TrackingDetails;
			public readonly bool IsCurrentRepositoryHead;
			public readonly bool IsRemote;
			public readonly string FriendlyName;

			public BranchInfo(Branch branch)
			{
				if (branch.TrackedBranch != null)
				{
					TrackedBranch = branch.TrackedBranch.FriendlyName;
				}
				TrackingDetails = branch.TrackingDetails;
				CanonicalName = branch.CanonicalName;
				Tip = branch.Tip;
				IsCurrentRepositoryHead = branch.IsCurrentRepositoryHead;
				IsRemote = branch.IsRemote;
				FriendlyName = branch.FriendlyName;
			}

			public Branch LoadBranch()
			{
				if (GitManager.Repository != null)
				{
					if (GitManager.Repository.Head.CanonicalName == CanonicalName)
					{
						return GitManager.Repository.Head;
					}
					if (GitManager.Repository.Branches != null)
					{
						return GitManager.Repository.Branches[CanonicalName];
					}
				}
				
				return null;
			}
		}

		public struct CommitInfo
		{
			public readonly ObjectId Id;
			public readonly Signature Committer;
			public readonly bool IsHead;
			public readonly bool IsRemote;
			public readonly BranchInfo[] Branches;
			public readonly string Message;

			public CommitInfo(Commit commit, BranchInfo[] branches) : this()
			{
				Id = commit.Id;
				Committer = commit.Committer;
				Message = commit.Message;

				if (branches.Length > 0)
				{
					Branches = branches;
					IsHead = branches.Any(b => b.IsCurrentRepositoryHead);
					IsRemote = branches.Any(b => b.IsRemote);
				}
			}
		}
	}
}