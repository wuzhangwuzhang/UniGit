﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LibGit2Sharp;
using UnityEditor;
using UnityEngine;

namespace UniGit
{
	public class GitWizardBase : ScriptableWizard
	{
		protected Remote[] remotes;
		protected GUIContent[] remoteNames;
		protected string[] branchNames;
		[SerializeField] protected Credentials credentials;
		[SerializeField]
		protected int selectedRemote;
		[SerializeField]
		protected int selectedBranch;
		[SerializeField] protected bool credentalsExpanded;
		private SerializedObject serializedObject;
		
		[SerializeField] private Vector2 logScroll;

		protected virtual void OnEnable()
		{
			remotes = GitManager.Repository.Network.Remotes.ToArray();
			remoteNames = remotes.Select(r => new GUIContent(r.Name)).ToArray();
			branchNames = GitManager.Repository.Branches.Select(b => b.CanonicalName).ToArray();
			serializedObject = new SerializedObject(this);
			Repaint();
		}

		public void Init(Branch branch)
		{
			branchNames = GitManager.Repository.Branches.Select(b => b.CanonicalName).ToArray();

			selectedRemote = Array.IndexOf(remotes, branch.Remote);
			selectedBranch = Array.IndexOf(branchNames, branch.CanonicalName);
		}

		protected void DrawRemoteSelection()
		{
			selectedRemote = EditorGUILayout.Popup(new GUIContent("Remote"), selectedRemote, remoteNames);
		}

		protected void DrawBranchSelection()
		{
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.PrefixLabel(new GUIContent("Branch"));
			selectedBranch = EditorGUILayout.Popup(selectedBranch, branchNames);
			EditorGUILayout.EndHorizontal();
		}

		protected void DrawCredentials()
		{
			credentalsExpanded = EditorGUILayout.PropertyField(serializedObject.FindProperty("credentials"));
			if (credentalsExpanded)
			{
				EditorGUI.indentLevel = 1;
				credentials.IsToken = EditorGUILayout.Toggle(new GUIContent("Is Token"), credentials.IsToken);
				if (credentials.IsToken)
				{
					credentials.Token = EditorGUILayout.TextField(new GUIContent("Token", "If left empty, stored credentials in settings will be used."), credentials.Token);
				}
				else
				{
					credentials.Username = EditorGUILayout.TextField(new GUIContent("Username", "If left empty, stored credentials in settings will be used."), credentials.Username);
					credentials.Password = EditorGUILayout.PasswordField(new GUIContent("Password", "If left empty, stored credentials in settings will be used."), credentials.Password);
				}
				EditorGUI.indentLevel = 0;
			}
		}

		protected override bool DrawWizardGUI()
		{
			EditorGUI.BeginChangeCheck();
			DrawRemoteSelection();
			DrawBranchSelection();
			DrawCredentials();
			return EditorGUI.EndChangeCheck();
		}

		#region Handlers
		protected LibGit2Sharp.Credentials CredentialsHandler(string url, string user, SupportedCredentialTypes supported)
		{
			if (supported == SupportedCredentialTypes.UsernamePassword)
			{
				string username = credentials.Username;
				string password = credentials.Password;

				if (credentials.IsToken)
				{
					username = credentials.Token;
					password = string.Empty;
				}

				if (GitManager.GitCredentials != null)
				{
					GitCredentials.Entry entry = GitManager.GitCredentials.GetEntry(url);
					if (entry != null)
					{
						if (entry.IsToken)
						{
							if (string.IsNullOrEmpty(username)) username = entry.Token.Trim();
							password = string.Empty;
						}
						else
						{
							if (string.IsNullOrEmpty(username)) username = entry.Username;
							if (string.IsNullOrEmpty(password)) password = entry.DecryptPassword();
						}
					}
				}

				return new UsernamePasswordCredentials()
				{
					Username = username,
					Password = password
				};
			}
			return new DefaultCredentials();
		}

		#region Fetch
		protected bool FetchTransferProgress(TransferProgress progress)
		{
			float percent = (float)progress.ReceivedObjects / progress.TotalObjects;
			bool cancel = EditorUtility.DisplayCancelableProgressBar("Transferring", string.Format("Transferring: Received total of: {0} bytes. {1}%", progress.ReceivedBytes, (percent * 100).ToString("###")), percent);
			if (progress.TotalObjects == progress.ReceivedObjects)
			{
				Debug.Log("Transfer Complete. Received a total of " + progress.IndexedObjects + " objects");
			}
			//true to continue
			return !cancel;
		}

		protected bool FetchProgress(string serverProgressOutput)
		{
			Debug.Log(string.Format("Fetching: {0}", serverProgressOutput));
			return true;
		}

		protected bool FetchOperationStarting(RepositoryOperationContext context)
		{
			Debug.Log("Fetch Operation Started");
			//true to continue
			return true;
		}

		protected void FetchOperationCompleted(RepositoryOperationContext context)
		{
			Debug.Log("Operation Complete");
		}
		#endregion

		#region Merge

		protected void OnMergeComplete(MergeResult result,string mergeType)
		{
			switch (result.Status)
			{
				case MergeStatus.UpToDate:
					GetWindow<GitHistoryWindow>().ShowNotification(new GUIContent(string.Format("Everything is Up to date. Nothing to {0}.", mergeType)));
					break;
				case MergeStatus.FastForward:
					GetWindow<GitHistoryWindow>().ShowNotification(new GUIContent(mergeType + " Complete with Fast Forwarding."));
					break;
				case MergeStatus.NonFastForward:
					GetWindow<GitDiffWindow>().ShowNotification(new GUIContent("Do a merge commit in order to push changes."));
					GetWindow<GitDiffWindow>().commitMessage = GitManager.Repository.Info.Message;
					Debug.Log(mergeType + " Complete without Fast Forwarding.");
					break;
				case MergeStatus.Conflicts:
					GUIContent content = EditorGUIUtility.IconContent("console.warnicon");
					content.text = "There are merge conflicts!";
					GetWindow<GitDiffWindow>().ShowNotification(content);
					GetWindow<GitDiffWindow>().commitMessage = GitManager.Repository.Info.Message;
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
			Debug.LogFormat("{0} Status: {1}", mergeType, result.Status);
		}

		protected bool OnCheckoutNotify(string path, CheckoutNotifyFlags notifyFlags)
		{
			Debug.Log(path + " (" + notifyFlags + ")");
			return true;
		}

		protected void OnCheckoutProgress(string path, int completedSteps, int totalSteps)
		{
			float percent = (float)completedSteps / totalSteps;
			EditorUtility.DisplayProgressBar("Checkout", string.Format("Checking {0} steps out of {1}.", completedSteps, totalSteps), percent);
		}
		#endregion
		#endregion

		[Serializable]
		protected struct Credentials
		{
			private string password;
			[SerializeField]
			private string username;
			[SerializeField] private bool isToken;
			[SerializeField] private string token;

			public string Password
			{
				get { return password; }
				set { password = value; }
			}

			public string Username
			{
				get { return username; }
				set { username = value; }
			}

			public bool IsToken
			{
				get { return isToken; }
				set { isToken = value; }
			}

			public string Token
			{
				get { return token; }
				set { token = value; }
			}
		}

		[Serializable]
		protected enum ConflictMergeType
		{
			Normal = 0,
			Ours = 1,
			Theirs = 2
		}
	}
}