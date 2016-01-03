﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Patchwork;
using Patchwork.Attributes;
using Patchwork.Utility;
using Patchwork.Utility.Binding;
using Serilog;
using Serilog.Events;

namespace PatchworkLauncher {

	public enum LaunchManagerState {
		GameRunning,
		IsPatching,
		Idle
	}

	public class LaunchManager {

		private guiHome _home;

		private guiMods _mods;

		private NotifyIcon _icon;

		private readonly OpenFileDialog _openModDialog = new OpenFileDialog {
			Filter = "Patchwork Mod Files (*.pw.dll)|*.pw.dll|DLL files (*.dll)|*.dll|All files (*.*)|*.*",
			CheckFileExists = true,
			CheckPathExists = true,
			Title = "Select Patchwork mod file",
			Multiselect = false,
			FilterIndex = 0,
			SupportMultiDottedExtensions = true,
			AutoUpgradeEnabled = true,
			InitialDirectory = PathHelper.GetAbsolutePath(""),
			RestoreDirectory = false
		};

		//these path specifications seem to be the most compatible between operating systems
		private static readonly string _pathHistoryXml = Path.Combine(".", "history.pw.xml");
		private static readonly string _pathSettings = Path.Combine(".", "instructions.pw.xml");
		private static readonly string _pathGameInfoAssembly = Path.Combine(".", "app.dll");
		private static readonly string _pathLogFile = Path.Combine(".", "log.txt");
		private static readonly XmlSerializer _historySerializer = new XmlSerializer(typeof (XmlHistory));
		private static readonly XmlSerializer _instructionSerializer = new XmlSerializer(typeof (XmlSettings));

		private DialogResult Command_Display_Warning(string text) {
			return MessageBox.Show(text, "Warning!", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}

		public LaunchManager() {
			//the following is needed on linux... the current directory must be the Mono executable, which is bad.
			Environment.CurrentDirectory = Path.GetDirectoryName (Assembly.GetExecutingAssembly ().Location);
			//TODO: Refactor this into a constructor?
			try {
				if (File.Exists(_pathLogFile)) {
					File.Delete(_pathLogFile);
				}
				Logger =
					new LoggerConfiguration().WriteTo.File(_pathLogFile, LogEventLevel.Debug).MinimumLevel.Debug().CreateLogger();
				var gameInfoFactory = PatchingHelper.LoadAppInfoFactory(_pathGameInfoAssembly);

				var settings = new XmlSettings();
				var history = new XmlHistory();
				;
				try {
					history = _historySerializer.Deserialize(_pathHistoryXml, new XmlHistory());
				}
				catch (Exception ex) {
					Command_Display_Error("Load patching history", _pathHistoryXml, ex,
						"If the launcher was terminated unexpectedly last time, it may not be able to recover.");
				}

				try {
					settings = _instructionSerializer.Deserialize(_pathSettings, new XmlSettings());
				}
				catch (Exception ex) {
					Command_Display_Error("Read settings file", _pathSettings, ex, "Patch list and other settings will be reset.");
				}
				string folderDialogReason = null;
				if (settings.BaseFolder == null) {
					folderDialogReason = "(no game folder has been specified)";
				} else if (!Directory.Exists(settings.BaseFolder)) {
					folderDialogReason = "(the previous game folder does not exist)";
				}
				if (folderDialogReason != null) {
					if (!Command_SetGameFolder_Dialog(folderDialogReason)) {
						Command_ExitApplication();
					}
				} else {
					BaseFolder = settings.BaseFolder;
				}
				
				
				AppInfo = gameInfoFactory.CreateInfo(new DirectoryInfo(BaseFolder));
				var icon = Icon.ExtractAssociatedIcon(AppInfo.Executable.FullName) ?? _home.Icon;
				ProgramIcon = icon.ToBitmap();
				Instructions = new DisposingBindingList<PatchInstruction>();
				var instructions = new List<XmlInstruction>();
				foreach (var xmlPatch in settings.Instructions) {
					try {
						Command_Direct_AddPatch(xmlPatch.PatchPath, xmlPatch.IsEnabled);
					}
					catch {
						instructions.Add(xmlPatch);
					}
				}
				var patchList = instructions.Select(x => x.PatchPath).Join(Environment.NewLine);
				if (patchList.Length > 0) {
					Command_Display_Error("Load patches on startup.", patchList);
				}
				try {
					PatchingHelper.RestorePatchedFiles(AppInfo, history.Files);
				}
				catch (Exception ex) {
					Command_Display_Error("Restore files", ex: ex);
				}
				//File.Delete(_pathHistoryXml);
				_home = new guiHome(this);
				_home.Closed += (sender, args) => Command_ExitApplication();
				_icon = new NotifyIcon {
					Icon = _home.Icon,
					Visible = false,
					Text = "Patchwork Launcher",
					ContextMenu = new ContextMenu {
						MenuItems = {
							new MenuItem("Quit", (o, e) => Command_ExitApplication())
						}
					}
				};
			}
			catch (Exception ex) {
				Command_Display_Error("Launch the application", ex: ex, message: "The application will now exit.");
				Command_ExitApplication();
			}
		}

		public ILogger Logger {
			get;
			set;
		}

		public DisposingBindingList<PatchInstruction> Instructions {
			get;
			private set;
		}

		public guiMods Command_OpenMods() {
			if (_mods == null) {
				_mods = new guiMods(this);
				_mods.Closing += (sender, args) => {
					if (_home?.Visible == true) {
						args.Cancel = true;
						_mods.Hide();
					}
				};
			}
			_mods.ShowOrFocus();
			return _mods;
		}

		public AppInfo AppInfo {
			get;
			set;
		}

		public string BaseFolder {
			get;
			set;
		}

		public int Command_MovePatch(int index, int offset) {

			if (index < 0 || index >= Instructions.Count) {
				throw new ArgumentException("Specified instruction was not in the sequence.");
			}
			var instruction = Instructions[index];
			var newIndex = index + offset;
			if (newIndex < 0 || newIndex >= Instructions.Count) {
				return index;
			}
			var oldOccupant = Instructions[newIndex];
			Instructions[index] = oldOccupant;
			Instructions[newIndex] = instruction;
			return newIndex;
		}

		public void Command_Dialog_AddPatch(IWin32Window owner) {
			var result = _openModDialog.ShowDialog(owner);
			if (result == DialogResult.Cancel) {
				return;
			}
			var fileName = _openModDialog.FileName;
			var fileNameOnly = Path.GetFileName(fileName);
			var collision =
				Instructions.SingleOrDefault(instr => Path.GetFileName(instr.PatchLocation).EqualsIgnoreCase(fileNameOnly));

			if (collision != null) {
				Command_Display_Error("Load a patch", fileNameOnly, message: "You already have a patch with this filename.");
				return;
			}
			try {
				Command_Direct_AddPatch(fileName, true);
			}
			catch (Exception ex) {
				Command_Display_Error("Load a patch", PathHelper.GetUserFriendlyPath(fileName), ex, "");
			}
		}

		public IBindable<LaunchManagerState> State {
			get;
		} = Bindable.Variable(LaunchManagerState.Idle);

		public async Task<XmlHistory> Command_Patch() {
			State.Value = LaunchManagerState.IsPatching;
			var history = new XmlHistory {
				Success = false
			};
			try {
				var progObj = new ProgressObject();
				using (var logForm = new LogForm(progObj)) {
					logForm.Show();
					try {
						var patches = GroupPatches(Instructions).ToList();
						history.Files = patches.Select(XmlFileHistory.FromInstrGroup).ToList();
						_historySerializer.Serialize(history, _pathHistoryXml);
						await Task.Run(() => ApplyInstructions(patches, progObj));
						history.Success = true;
					}
					catch (PatchingProcessException ex) {
						Command_Display_Patching_Error(ex);
					}
					if (!history.Success) {
						PatchingHelper.RestorePatchedFiles(AppInfo, history.Files);
					}
					logForm.Close();
				}
			}
			catch (Exception ex) {
				Command_Display_Error("Patch the game", ex: ex);
			}
			finally {
				State.Value = LaunchManagerState.Idle;
				if (DebugOptions.Default.OpenLogAfterPatch) {
					Process.Start(_pathLogFile);
				}
			}
			return history;
		}

		public async void Command_Launch_Modded() {
			Action<IBindable<LaunchManagerState>> p = null;
			var history = await Command_Patch();
			p = v => {
				if (v.Value == LaunchManagerState.Idle) {
					PatchingHelper.RestorePatchedFiles(AppInfo, history.Files);
					State.HasChanged -= p;
				}
			};
			State.HasChanged += p;
			Command_Launch();
		}

		public void Command_ChangeFolder() {
			if (Command_SetGameFolder_Dialog("")) {
				Command_ExitApplication();
			}
		}

		public void Command_Launch() {
			if (DebugOptions.Default.DontRunProgram) {
				State.Value = LaunchManagerState.Idle;
				return;
			}
			var process = new Process {
				StartInfo = {
					FileName = AppInfo.Executable.FullName,
					WorkingDirectory = Path.GetDirectoryName(AppInfo.Executable.FullName)
				},
				EnableRaisingEvents = true
			};
			process.Exited += delegate {
				State.Value = LaunchManagerState.Idle;
			};

			State.HasChanged += delegate {
				_home.Invoke((Action) (() => {
					if (State.Value == LaunchManagerState.GameRunning) {
						_home.Hide();
						_icon.Visible = true;
						_icon.ShowBalloonTip(1000, "Launching",
							"Launching the application. The launcher will remain in the tray for cleanup.",
							ToolTipIcon.Info);
					} else {
						Command_ExitApplication();
					}
				}));
			};
			State.Value = process.Start() ? LaunchManagerState.GameRunning : LaunchManagerState.Idle;
		}

		public Bitmap ProgramIcon {
			get;
			private set;
		}

		public PatchInstruction Command_Direct_AddPatch(string path, bool isEnabled) {
			var targetPath = path;
			var fileName = Path.GetFileName(path);

			var hadToCopy = false;
			PatchingManifest manifest = null;
			try {
				Directory.CreateDirectory(_modFolder);
				var folder = Path.GetDirectoryName(path);
				var absoluteFolder = PathHelper.GetAbsolutePath(folder);
				var modsPath = PathHelper.GetAbsolutePath(_modFolder);

				if (!DebugOptions.Default.DontCopyFiles
					&& !modsPath.Equals(absoluteFolder, StringComparison.InvariantCultureIgnoreCase)) {
					targetPath = Path.Combine(_modFolder, fileName);
					File.Copy(path, targetPath, true);
					hadToCopy = true;
				}
				manifest = ManifestMaker.CreateManifest(PathHelper.GetAbsolutePath(targetPath));
				if (manifest.PatchInfo == null) {
					throw new PatchDeclerationException("The patch did not have a PatchInfo class.");
				}
				var patchInstruction = new PatchInstruction {
					IsEnabled = isEnabled,
					Patch = manifest,
					PatchLocation = PathHelper.GetRelativePath(targetPath),
					AppInfo = AppInfo,
					PatchOriginalLocation = path
				};
				Instructions.Add(patchInstruction);
				return patchInstruction;
			}
			catch (Exception ex) {
				Logger.Error(ex, $"The patch located in {path} could not be loaded.");
				manifest?.Dispose();
				if (hadToCopy) {
					File.Delete(targetPath);
				}
				throw;
			}
		}

		public void Command_Restore() {

			_home.Visible = true;
			_icon.Visible = false;
		}

		public guiHome Command_Start() {
			_home.ShowOrFocus();
			return _home;
		}

		public void Command_ExitApplication() {
			_icon?.Dispose();
			var xmlInstructions = XmlSettings.FromInstructionSeq(Instructions);
			xmlInstructions.BaseFolder = BaseFolder;
			_instructionSerializer?.Serialize(xmlInstructions, _pathSettings);
			if (Application.MessageLoop) {
				Application.Exit();
			} else {
				Environment.Exit(0);
			}
		}

		public async void Command_TestRun() {
			DebugOptions.Default.OpenLogAfterPatch = true;
			var history = await Command_Patch();
			PatchingHelper.RestorePatchedFiles(AppInfo, history.Files);
		}

		private ManifestCreator ManifestMaker {
			get;
		} = new ManifestCreator();

		private const string _modFolder = "Mods";


		private void Command_Display_Patching_Error(PatchingProcessException ex) {
			var targetFile = ex.TargetFile;
			var thePatch = ex.AssociatedInstruction?.Name;
			var objectsThatFailed = "";
			if (targetFile != null && thePatch != null) {
				objectsThatFailed = $"{thePatch} ⇒ {targetFile}";
			} else if (targetFile != null) {
				objectsThatFailed = targetFile;
			}
			var tryingToDoWhat = ex.Step.GetEnumValueText() ?? "Patch a file";
			Command_Display_Error(tryingToDoWhat, objectsThatFailed, ex.InnerException);
		}

		private bool Command_SetGameFolder_Dialog(string warning) {
			var wasHomeDisabled = false;
			try {
				using (var input = new guiInputGameFolder(warning)) {
					if (_home?.Visible == true) {
						_home.Enabled = false;
						wasHomeDisabled = true;
					}
					var result = input.ShowDialog();
					if (result == DialogResult.OK) {
						BaseFolder = input.Folder.Value;
						return true;
					}
					return false;
				}
			}
			finally {
				if (wasHomeDisabled) {
					_home.Enabled = true;
				}
			}
		}

		private DialogResult Command_Display_Error(string tryingToDoWhat, string objectsThatFailed = null, Exception ex = null,
			string message = null) {
			//TODO: Better error dialog
			var errorType = "";
			if (ex is PatchException) {
				errorType = "A patch was invalid, incompatible, or caused an error.";
			} else if (ex is IOException) {
				errorType = "Related to reading/writing files.";
			} else if (ex is ApplicationException) {
				errorType = "An application error.";
			} else if (ex != null) {
				errorType = "A system error or some sort of bug.";
			}
			var errorString = "An error has occurred,\r\n";
			errorString += tryingToDoWhat.IsNullOrWhitespace() ? "" : $"While trying to: {tryingToDoWhat}\r\n";
			errorString += errorType.IsNullOrWhitespace() ? "" : $"Error type: {errorType} ({ex?.GetType().Name})\r\n";
			errorString += ex == null ? "" : $"Internal message: {ex.Message}\r\n";
			errorString += objectsThatFailed.IsNullOrWhitespace() ? "" : $"Object(s) that failed: {objectsThatFailed}\r\n";
			errorString += message.IsNullOrWhitespace() ? "" : $"{message}\r\n";
			Logger.Error(ex, errorString);
			return MessageBox.Show(errorString, "Error!", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		private IEnumerable<PatchGroup> GroupPatches(IEnumerable<PatchInstruction> instrs) {
			var dict = new Dictionary<string, List<PatchInstruction>>();

			foreach (var instr in instrs) {
				try {
					var canPatch = instr.Patch.PatchInfo.CanPatch(AppInfo);
					if (canPatch != null) {
						throw new PatchExecutionException(canPatch);
					}
					var targetFile = instr.Patch.PatchInfo.GetTargetFile(AppInfo).FullName;
					if (dict.ContainsKey(targetFile)) {
						dict[targetFile].Add(instr);
					} else {
						dict[targetFile] = new List<PatchInstruction>(new[] {
							instr
						});
					}
				}
				catch (Exception ex) {
					throw new PatchingProcessException(ex) {
						AssociatedInstruction = instr,
						TargetFile = null,
						Step = PatchProcessingStep.Grouping
					};
				}
			}
			var groups =
				from kvp in dict
				select new PatchGroup {
					Instructions = kvp.Value,
					TargetPath = kvp.Key
				};

			return groups.ToList();
		}

		private void ApplyInstructions(IEnumerable<PatchGroup> patchGroups, ProgressObject po) {
			//TODO: Use a different progress tracking system and make the entire patching operation more recoverable and fault-tolerant.
			//TODO: Refactor this method.
			patchGroups = patchGroups.ToList();
			var appInfo = AppInfo;
			var logger = Logger;
			var fileProgress = new ProgressObject();
			po.Child.Value = fileProgress;
			var patchProgress = new ProgressObject();
			fileProgress.Child.Value = patchProgress;
			var myAttributesAssembly = typeof (AppInfo).Assembly;
			var attributesAssemblyName = Path.GetFileName(myAttributesAssembly.Location);
			var history = new List<XmlFileHistory>();
			po.TaskTitle.Value = "Patching Game";
			po.TaskText.Value = appInfo.AppName;
			po.Total.Value = patchGroups.Count();

			foreach (var patchGroup in patchGroups) {
				var patchCount = patchGroup.Instructions.Count;
				po.TaskTitle.Value = $"Patching {appInfo.AppName}";
				var targetFile = patchGroup.TargetPath;
				po.TaskText.Value = Path.GetFileName(targetFile);
				//Note that Path.Combine(FILENAME, "..", OTHER_FILENAME) doesn't work on Mono but does work on .NET.
				var dir = Path.GetDirectoryName(targetFile);

				var localAssemblyName = Path.Combine(dir, attributesAssemblyName);
				var copy = true;
				fileProgress.TaskTitle.Value = "Patching File";
				fileProgress.Total.Value = 2 + patchCount;
				fileProgress.Current.Value++;

				var backupModified = PatchingHelper.GetBackupForModified(targetFile);
				var backupOrig = PatchingHelper.GetBackupForOriginal(targetFile);
				fileProgress.TaskText.Value = "Applying Patch";

				if (!PatchingHelper.DoesFileMatchPatchList(backupModified, targetFile, patchGroup.Instructions)
					|| DebugOptions.Default.AlwaysPatch) {
					if (File.Exists(localAssemblyName)) {
						try {
							var localAssembly = AssemblyCache.Default.ReadAssembly(localAssemblyName);
							if (localAssembly.GetAssemblyMetadataString() == myAttributesAssembly.GetAssemblyMetadataString()) {
								copy = false;
							}
						}
						catch (Exception ex) {
							Logger.Warning(ex, $"Failed to read local attributes assembly so it will be overwritten.");
							//if reading the assembly failed for any reason, just ignore...
						}
					}
					if (copy) {
						File.Copy(myAttributesAssembly.Location, localAssemblyName, true);
					}

					var patcher = new AssemblyPatcher(targetFile, logger) {
						EmbedHistory = true
					};

					foreach (var patch in patchGroup.Instructions) {
						try {
							patcher.PatchManifest(patch.Patch, patchProgress);
						}
						catch (PatchException ex) {
							throw new PatchingProcessException(ex) {
								AssociatedInstruction = patch,
								AssociatedPatchGroup = patchGroup,
								Step = PatchProcessingStep.ApplyingSpecificPatch
							};
						}
						fileProgress.Current.Value++;
					}
					patchProgress.TaskText.Value = "";
					patchProgress.TaskTitle.Value = "";


					fileProgress.Current.Value++;
					fileProgress.TaskText.Value = "Writing Assembly";
					if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
						fileProgress.TaskText.Value = "Running PEVerify...";
						var targetFolder = Path.GetDirectoryName(targetFile);
						try {
							var peOutput = patcher.RunPeVerify(new PEVerifyInput {
								AssemblyResolutionFolder = targetFolder,
								IgnoreErrors = AppInfo.IgnorePEVerifyErrors.ToList()
							});
							logger.Information(peOutput.Raw);
						}
						catch (Exception ex) {
							logger.Error(ex, "Failed to run PEVerify on the assembly.");
						}
					}

					try {
						patcher.WriteTo(backupModified);
					}
					catch (Exception ex) {
						throw new PatchingProcessException(ex) {
							AssociatedInstruction = null,
							AssociatedPatchGroup = patchGroup,
							Step = PatchProcessingStep.WritingToFile
						};
					}
				} else {
					fileProgress.Current.Value += patchCount;
				}
				try {
					PatchingHelper.SwitchFilesSafely(backupModified, targetFile, backupOrig);
				}
				catch (Exception ex) {
					throw new PatchingProcessException(ex) {
						AssociatedInstruction = null,
						AssociatedPatchGroup = patchGroup,
						Step = PatchProcessingStep.PerformingSwitch
					};
				}
				AssemblyCache.Default.Clear();
				po.Current.Value++;
			}
		}
	}
}