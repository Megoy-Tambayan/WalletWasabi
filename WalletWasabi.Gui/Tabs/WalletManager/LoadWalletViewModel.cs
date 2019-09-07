using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvalonStudio.Extensibility;
using AvalonStudio.Shell;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Gui.Controls.WalletExplorer;
using WalletWasabi.Gui.Dialogs;
using WalletWasabi.Gui.Models;
using WalletWasabi.Gui.ViewModels;
using WalletWasabi.Helpers;
using WalletWasabi.Hwi2;
using WalletWasabi.Hwi2.Models;
using WalletWasabi.KeyManagement;
using WalletWasabi.Logging;

namespace WalletWasabi.Gui.Tabs.WalletManager
{
	internal class LoadWalletViewModel : CategoryViewModel
	{
		private ObservableCollection<LoadWalletEntry> _wallets;
		private string _password;
		private LoadWalletEntry _selectedWallet;
		private bool _isWalletSelected;
		private bool _isWalletOpened;
		private bool _canLoadWallet;
		private bool _canTestPassword;
		private string _warningMessage;
		private string _validationMessage;
		private string _successMessage;
		private bool _isBusy;
		private bool _isHardwareBusy;
		private string _loadButtonText;
		private bool _isHwWalletSearchTextVisible;

		public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

		private WalletManagerViewModel Owner { get; }
		public Global Global => Owner.Global;
		public LoadWalletType LoadWalletType { get; }

		public bool IsPasswordRequired => LoadWalletType == LoadWalletType.Password;
		public bool IsHardwareWallet => LoadWalletType == LoadWalletType.Hardware;
		public bool IsDesktopWallet => LoadWalletType == LoadWalletType.Desktop;

		public LoadWalletViewModel(WalletManagerViewModel owner, LoadWalletType loadWalletType)
			: base(loadWalletType == LoadWalletType.Password ? "Test Password" : (loadWalletType == LoadWalletType.Desktop ? "Load Wallet" : "Hardware Wallet"))
		{
			Owner = owner;
			Password = "";
			LoadWalletType = loadWalletType;
			Wallets = new ObservableCollection<LoadWalletEntry>();
			IsHwWalletSearchTextVisible = false;

			this.WhenAnyValue(x => x.SelectedWallet)
				.Subscribe(_ => TrySetWalletStates());

			this.WhenAnyValue(x => x.IsWalletOpened)
				.Subscribe(_ => TrySetWalletStates());

			this.WhenAnyValue(x => x.IsBusy)
				.Subscribe(_ => TrySetWalletStates());

			this.WhenAnyValue(x => x.Password).Subscribe(async x =>
			{
				try
				{
					if (x.NotNullAndNotEmpty())
					{
						char lastChar = x.Last();
						if (lastChar == '\r' || lastChar == '\n') // If the last character is cr or lf then act like it'd be a sign to do the job.
						{
							Password = x.TrimEnd('\r', '\n');
							await LoadKeyManagerAsync(requirePassword: true, isHardwareWallet: false);
						}
					}
				}
				catch (Exception ex)
				{
					Logger.LogTrace(ex);
				}
			});

			LoadCommand = ReactiveCommand.CreateFromTask(async () => await LoadWalletAsync(), this.WhenAnyValue(x => x.CanLoadWallet));
			TestPasswordCommand = ReactiveCommand.CreateFromTask(async () => await LoadKeyManagerAsync(requirePassword: true, isHardwareWallet: false), this.WhenAnyValue(x => x.CanTestPassword));
			OpenFolderCommand = ReactiveCommand.Create(OpenWalletsFolder);
			ImportColdcardCommand = ReactiveCommand.CreateFromTask(async () =>
			{
				var ofd = new OpenFileDialog
				{
					AllowMultiple = false,
					Title = "Import Coldcard"
				};

				if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
				}

				var selected = await ofd.ShowAsync(Application.Current.MainWindow, fallBack: true);
				if (selected != null && selected.Any())
				{
					var path = selected.First();
					var jsonString = await File.ReadAllTextAsync(path);
					var json = JObject.Parse(jsonString);
					var xpubString = json["ExtPubKey"].ToString();
					var mfpString = json["MasterFingerprint"].ToString();

					// https://github.com/zkSNACKs/WalletWasabi/pull/1663#issuecomment-508073066
					// Coldcard 2.1.0 improperly implemented Wasabi skeleton fingerpring at first, so we must reverse byte order.
					// The solution was to add a ColdCardFirmwareVersion json field from 2.1.1 and correct the one generated by 2.1.0.
					var coldCardVersionString = json["ColdCardFirmwareVersion"]?.ToString();
					var reverseByteOrder = false;
					if (coldCardVersionString is null)
					{
						reverseByteOrder = true;
					}
					else
					{
						Version coldCardVersion = new Version(coldCardVersionString);

						if (coldCardVersion == new Version("2.1.0")) // Should never happen though.
						{
							reverseByteOrder = true;
						}
					}
					HDFingerprint mfp = NBitcoinHelpers.BetterParseHDFingerprint(mfpString, reverseByteOrder: reverseByteOrder);
					ExtPubKey extPubKey = NBitcoinHelpers.BetterParseExtPubKey(xpubString);
					Logger.LogInfo<LoadWalletViewModel>("Creating new wallet file.");
					var walletName = Global.GetNextHardwareWalletName(customPrefix: "Coldcard");
					var walletFullPath = Global.GetWalletFullPath(walletName);
					KeyManager.CreateNewHardwareWalletWatchOnly(mfp, extPubKey, walletFullPath);
					owner.SelectLoadWallet();
				}
			});

			EnumerateHardwareWalletsCommand = ReactiveCommand.CreateFromTask(async () => await EnumerateHardwareWalletsAsync());

			OpenBrowserCommand = ReactiveCommand.Create<string>(x =>
			{
				IoHelpers.OpenBrowser(x);
			});

			OpenBrowserCommand.ThrownExceptions.Subscribe(Logger.LogWarning<LoadWalletViewModel>);
			LoadCommand.ThrownExceptions.Subscribe(Logger.LogWarning<LoadWalletViewModel>);
			TestPasswordCommand.ThrownExceptions.Subscribe(Logger.LogWarning<LoadWalletViewModel>);
			OpenFolderCommand.ThrownExceptions.Subscribe(ex =>
			{
				SetWarningMessage(ex.ToTypeMessageString());
				Logger.LogError<LoadWalletViewModel>(ex);
			});
			ImportColdcardCommand.ThrownExceptions.Subscribe(ex =>
			{
				SetWarningMessage(ex.ToTypeMessageString());
				Logger.LogError<LoadWalletViewModel>(ex);
			});
			EnumerateHardwareWalletsCommand.ThrownExceptions.Subscribe(ex =>
			{
				SetWarningMessage(ex.ToTypeMessageString());
				Logger.LogError<LoadWalletViewModel>(ex);
			});

			SetLoadButtonText();
		}

		public string UDevRulesLink => "https://github.com/bitcoin-core/HWI/tree/master/udev";

		public bool IsHwWalletSearchTextVisible
		{
			get => _isHwWalletSearchTextVisible;
			set => this.RaiseAndSetIfChanged(ref _isHwWalletSearchTextVisible, value);
		}

		public ObservableCollection<LoadWalletEntry> Wallets
		{
			get => _wallets;
			set => this.RaiseAndSetIfChanged(ref _wallets, value);
		}

		public string Password
		{
			get => _password;
			set => this.RaiseAndSetIfChanged(ref _password, value);
		}

		public LoadWalletEntry SelectedWallet
		{
			get => _selectedWallet;
			set => this.RaiseAndSetIfChanged(ref _selectedWallet, value);
		}

		public bool IsWalletSelected
		{
			get => _isWalletSelected;
			set => this.RaiseAndSetIfChanged(ref _isWalletSelected, value);
		}

		public bool IsWalletOpened
		{
			get => _isWalletOpened;
			set => this.RaiseAndSetIfChanged(ref _isWalletOpened, value);
		}

		public string WarningMessage
		{
			get => _warningMessage;
			set => this.RaiseAndSetIfChanged(ref _warningMessage, value);
		}

		public string ValidationMessage
		{
			get => _validationMessage;
			set => this.RaiseAndSetIfChanged(ref _validationMessage, value);
		}

		public string SuccessMessage
		{
			get => _successMessage;
			set => this.RaiseAndSetIfChanged(ref _successMessage, value);
		}

		public void SetWarningMessage(string message)
		{
			WarningMessage = message;
			ValidationMessage = "";
			SuccessMessage = "";
		}

		public void SetValidationMessage(string message)
		{
			WarningMessage = "";
			ValidationMessage = message;
			SuccessMessage = "";
		}

		public void SetLoadButtonText()
		{
			if (IsHardwareBusy)
			{
				LoadButtonText = "Waiting for Hardware Wallet...";
			}
			else if (IsBusy)
			{
				LoadButtonText = "Loading...";
			}
			else
			{
				// If the hardware wallet was not initialized, then make the button say Setup, not Load.
				LoadButtonText = SelectedWallet?.HardwareWalletInfo != null && !SelectedWallet.HardwareWalletInfo.IsInitialized()
					? "Setup Wallet"
					: "Load Wallet";
			}
		}

		public string LoadButtonText
		{
			get => _loadButtonText;
			set => this.RaiseAndSetIfChanged(ref _loadButtonText, value);
		}

		public bool CanLoadWallet
		{
			get => _canLoadWallet;
			set => this.RaiseAndSetIfChanged(ref _canLoadWallet, value);
		}

		public bool CanTestPassword
		{
			get => _canTestPassword;
			set => this.RaiseAndSetIfChanged(ref _canTestPassword, value);
		}

		public bool IsBusy
		{
			get => _isBusy;
			set => this.RaiseAndSetIfChanged(ref _isBusy, value);
		}

		public bool IsHardwareBusy
		{
			get => _isHardwareBusy;
			set
			{
				this.RaiseAndSetIfChanged(ref _isHardwareBusy, value);

				try
				{
					TrySetWalletStates();
				}
				catch (Exception ex)
				{
					Logger.LogInfo<LoadWalletViewModel>(ex);
				}
			}
		}

		public override void OnCategorySelected()
		{
			if (IsHardwareWallet)
			{
				return;
			}

			Wallets.Clear();
			Password = "";
			SetValidationMessage("");

			var directoryInfo = new DirectoryInfo(Global.WalletsDir);
			var walletFiles = directoryInfo.GetFiles("*.json", SearchOption.TopDirectoryOnly).OrderByDescending(t => t.LastAccessTimeUtc);
			foreach (var file in walletFiles)
			{
				var wallet = new LoadWalletEntry(Path.GetFileNameWithoutExtension(file.FullName));
				if (IsPasswordRequired)
				{
					if (KeyManager.TryGetEncryptedSecretFromFile(file.FullName, out _))
					{
						Wallets.Add(wallet);
					}
				}
				else
				{
					Wallets.Add(wallet);
				}
			}

			SelectedWallet = Wallets.FirstOrDefault();
			TrySetWalletStates();
		}

		private bool TrySetWalletStates()
		{
			try
			{
				IsWalletSelected = SelectedWallet != null;
				CanTestPassword = IsWalletSelected;

				if (Global.WalletService is null)
				{
					IsWalletOpened = false;

					// If not busy loading.
					// And wallet is selected.
					// And no wallet is opened.
					CanLoadWallet = !IsBusy && IsWalletSelected;
				}
				else
				{
					IsWalletOpened = true;
					CanLoadWallet = false;
					SetWarningMessage("There is already an open wallet. Restart the application in order to open a different one.");
				}

				SetLoadButtonText();
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning<LoadWalletViewModel>(ex);
			}

			return false;
		}

		public ReactiveCommand<Unit, Unit> LoadCommand { get; }
		public ReactiveCommand<Unit, KeyManager> TestPasswordCommand { get; }
		public ReactiveCommand<Unit, Unit> ImportColdcardCommand { get; set; }
		public ReactiveCommand<Unit, Unit> EnumerateHardwareWalletsCommand { get; set; }
		public ReactiveCommand<string, Unit> OpenBrowserCommand { get; }

		public async Task<KeyManager> LoadKeyManagerAsync(bool requirePassword, bool isHardwareWallet)
		{
			try
			{
				SetValidationMessage("");
				CanTestPassword = false;
				var password = Guard.Correct(Password); // Do not let whitespaces to the beginning and to the end.
				Password = ""; // Clear password field.

				var selectedWallet = SelectedWallet;
				if (selectedWallet is null)
				{
					SetValidationMessage("No wallet selected.");
					return null;
				}

				var walletName = selectedWallet.WalletName;
				if (isHardwareWallet)
				{
					using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
					{
						var client = new HwiClient(Global.Network);

						if (selectedWallet.HardwareWalletInfo is null)
						{
							SetValidationMessage("No hardware wallets detected.");
							return null;
						}

						if (!selectedWallet.HardwareWalletInfo.IsInitialized())
						{
							try
							{
								IsHardwareBusy = true;
								MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusBarStatus.SettingUpHardwareWallet);
								// After HWI 1.0.1 the openconsole can be made false for Trezor T.
								// HWI will start detecting the exact type of hardware wallets.
								await client.SetupAsync(selectedWallet.HardwareWalletInfo.Type, selectedWallet.HardwareWalletInfo.Path, true, cts.Token);

								MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusBarStatus.ConnectingToHardwareWallet);
								await EnumerateHardwareWalletsAsync();
							}
							finally
							{
								IsHardwareBusy = false;
								MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusBarStatus.SettingUpHardwareWallet, StatusBarStatus.ConnectingToHardwareWallet);
							}

							return await LoadKeyManagerAsync(requirePassword, isHardwareWallet);
						}
						else if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
						{
							await client.PromptPinAsync(selectedWallet.HardwareWalletInfo.Type, selectedWallet.HardwareWalletInfo.Path, cts.Token);

							PinPadViewModel pinpad = IoC.Get<IShell>().Documents.OfType<PinPadViewModel>().FirstOrDefault();
							if (pinpad is null)
							{
								pinpad = new PinPadViewModel(Global);
								IoC.Get<IShell>().AddOrSelectDocument(pinpad);
							}
							var result = await pinpad.ShowDialogAsync();
							if (!(result is true))
							{
								SetValidationMessage("PIN was not provided.");
								return null;
							}

							var maskedPin = pinpad.MaskedPin;

							await client.SendPinAsync(selectedWallet.HardwareWalletInfo.Type, selectedWallet.HardwareWalletInfo.Path, int.Parse(maskedPin), cts.Token);

							var p = selectedWallet.HardwareWalletInfo.Path;
							var t = selectedWallet.HardwareWalletInfo.Type;
							await EnumerateHardwareWalletsAsync();
							selectedWallet = Wallets.FirstOrDefault(x => x.HardwareWalletInfo.Type == t && x.HardwareWalletInfo.Path == p);
							if (selectedWallet is null)
							{
								SetValidationMessage("Could not find the hardware wallet you are working with. Did you disconnect it?");
								return null;
							}
							else
							{
								SelectedWallet = selectedWallet;
							}

							if (!selectedWallet.HardwareWalletInfo.IsInitialized())
							{
								SetValidationMessage("Hardware wallet is not initialized.");
								return null;
							}

							if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
							{
								SetValidationMessage("Hardware wallet needs a PIN to be sent.");
								return null;
							}
						}

						if (selectedWallet.HardwareWalletInfo.Fingerprint is null)
						{
							throw new InvalidOperationException("Hardware wallet did not provide a master fingerprint.");
						}

						ExtPubKey extPubKey;
						try
						{
							MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusBarStatus.AcquiringXpubFromHardwareWallet);
							extPubKey = await client.GetXpubAsync(selectedWallet.HardwareWalletInfo.Fingerprint.Value, KeyManager.DefaultAccountKeyPath, cts.Token);
						}
						finally
						{
							MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusBarStatus.AcquiringXpubFromHardwareWallet);
						}

						Logger.LogInfo<LoadWalletViewModel>("Hardware wallet was not used previously on this computer. Creating new wallet file.");

						if (TryFindWalletByExtPubKey(extPubKey, out string wn))
						{
							walletName = wn;
						}
						else
						{
							walletName = Global.GetNextHardwareWalletName(selectedWallet.HardwareWalletInfo);
							var path = Global.GetWalletFullPath(walletName);
							KeyManager.CreateNewHardwareWalletWatchOnly(selectedWallet.HardwareWalletInfo.Fingerprint.Value, extPubKey, path);
						}
					}
				}

				var walletFullPath = Global.GetWalletFullPath(walletName);
				var walletBackupFullPath = Global.GetWalletBackupFullPath(walletName);
				if (!File.Exists(walletFullPath) && !File.Exists(walletBackupFullPath))
				{
					// The selected wallet is not available any more (someone deleted it?).
					OnCategorySelected();
					SetValidationMessage("The selected wallet and its backup do not exist, did you delete them?");
					return null;
				}

				KeyManager keyManager = Global.LoadKeyManager(walletFullPath, walletBackupFullPath);

				// Only check requirepassword here, because the above checks are applicable to loadwallet, too and we are using this function from load wallet.
				if (requirePassword)
				{
					if (PasswordHelper.TryPassword(keyManager, password, out string compatibilityPasswordUsed))
					{
						SuccessMessage = "Correct password.";
						if (compatibilityPasswordUsed != null)
						{
							WarningMessage = PasswordHelper.CompatibilityPasswordWarnMessage;
							ValidationMessage = "";
						}

						keyManager.SetPasswordVerified();
					}
					else
					{
						SetValidationMessage("Wrong password.");
						return null;
					}
				}
				else
				{
					if (keyManager.PasswordVerified == false)
					{
						Owner.SelectTestPassword();
						return null;
					}
				}

				return keyManager;
			}
			catch (Exception ex)
			{
				try
				{
					await EnumerateHardwareWalletsAsync();
				}
				catch (Exception ex2)
				{
					Logger.LogError<LoadWalletViewModel>(ex2);
				}

				// Initialization failed.
				SetValidationMessage(ex.ToTypeMessageString());
				Logger.LogError<LoadWalletViewModel>(ex);

				return null;
			}
			finally
			{
				CanTestPassword = IsWalletSelected;
			}
		}

		private async Task EnumerateHardwareWalletsAsync()
		{
			var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
			IsHwWalletSearchTextVisible = true;
			try
			{
				var client = new HwiClient(Global.Network);
				var devices = await client.EnumerateAsync(cts.Token);

				Wallets.Clear();
				foreach (var dev in devices)
				{
					var walletEntry = new LoadWalletEntry(dev);
					Wallets.Add(walletEntry);
				}
				TrySetWalletStates();
			}
			finally
			{
				IsHwWalletSearchTextVisible = false;
				cts.Dispose();
			}
		}

		private bool TryFindWalletByExtPubKey(ExtPubKey extPubKey, out string walletName)
		{
			// Start searching for the real wallet name.
			walletName = null;

			var walletFiles = new DirectoryInfo(Global.WalletsDir);
			var walletBackupFiles = new DirectoryInfo(Global.WalletBackupsDir);

			List<FileInfo> walletFileNames = new List<FileInfo>();

			if (walletFiles.Exists)
			{
				walletFileNames.AddRange(walletFiles.EnumerateFiles());
			}

			if (walletBackupFiles.Exists)
			{
				walletFileNames.AddRange(walletFiles.EnumerateFiles());
			}

			walletFileNames = walletFileNames.OrderByDescending(x => x.LastAccessTimeUtc).ToList();

			foreach (FileInfo walletFile in walletFileNames)
			{
				if (walletFile?.Extension?.Equals(".json", StringComparison.OrdinalIgnoreCase) is true
					&& KeyManager.TryGetExtPubKeyFromFile(walletFile.FullName, out ExtPubKey epk))
				{
					if (epk == extPubKey) // We already had it.
					{
						walletName = walletFile.Name;
						return true;
					}
				}
			}

			return false;
		}

		public async Task LoadWalletAsync()
		{
			try
			{
				IsBusy = true;
				SetValidationMessage("");
				MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusBarStatus.Loading);

				var keyManager = await LoadKeyManagerAsync(IsPasswordRequired, IsHardwareWallet);
				if (keyManager is null)
				{
					return;
				}

				try
				{
					await Task.Run(async () =>
					{
						await Global.InitializeWalletServiceAsync(keyManager);
					});
					// Successffully initialized.
					Owner.OnClose();
					// Open Wallet Explorer tabs
					if (Global.WalletService.Coins.Any())
					{
						// If already have coins then open with History tab first.
						IoC.Get<WalletExplorerViewModel>().OpenWallet(Global.WalletService, receiveDominant: false);
					}
					else // Else open with Receive tab first.
					{
						IoC.Get<WalletExplorerViewModel>().OpenWallet(Global.WalletService, receiveDominant: true);
					}
				}
				catch (Exception ex)
				{
					// Initialization failed.
					SetValidationMessage(ex.ToTypeMessageString());
					if (!(ex is OperationCanceledException))
					{
						Logger.LogError<LoadWalletViewModel>(ex);
					}
					await Global.DisposeInWalletDependentServicesAsync();
				}
			}
			finally
			{
				MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusBarStatus.Loading);
				IsBusy = false;
			}
		}

		public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }

		public void OpenWalletsFolder()
		{
			var path = Global.WalletsDir;
			IoHelpers.OpenFolderInFileExplorer(path);
		}
	}
}
