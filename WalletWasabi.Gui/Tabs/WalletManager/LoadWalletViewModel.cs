using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Gui.Controls.WalletExplorer;
using WalletWasabi.Gui.Dialogs;
using WalletWasabi.Gui.Helpers;
using WalletWasabi.Gui.Models;
using WalletWasabi.Gui.Models.StatusBarStatuses;
using WalletWasabi.Gui.ViewModels;
using WalletWasabi.Gui.ViewModels.Validation;
using WalletWasabi.Helpers;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Logging;
using WalletWasabi.Models;

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

				if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				{
					ofd.Directory = Path.Combine("/media", Environment.UserName);
				}
				else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				{
					ofd.Directory = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
				}

				var window = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime).MainWindow;
				var selected = await ofd.ShowAsync(window, fallBack: true);
				if (selected != null && selected.Any())
				{
					var path = selected.First();
					var jsonString = await File.ReadAllTextAsync(path);
					var json = JObject.Parse(jsonString);
					var xpubString = json["ExtPubKey"].ToString();
					var mfpString = json["MasterFingerprint"].ToString();

					// https://github.com/zkSNACKs/WalletWasabi/pull/1663#issuecomment-508073066
					// Coldcard 2.1.0 improperly implemented Wasabi skeleton fingerprint at first, so we must reverse byte order.
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
					Logger.LogInfo("Creating a new wallet file.");
					var walletName = Global.GetNextHardwareWalletName(customPrefix: "Coldcard");
					var walletFullPath = Global.GetWalletFullPath(walletName);
					KeyManager.CreateNewHardwareWalletWatchOnly(mfp, extPubKey, walletFullPath);
					owner.SelectLoadWallet();
				}
			});

			EnumerateHardwareWalletsCommand = ReactiveCommand.CreateFromTask(async () => await EnumerateHardwareWalletsAsync());

			OpenBrowserCommand = ReactiveCommand.Create<string>(x => IoHelpers.OpenBrowser(x));

			Observable
				.Merge(OpenBrowserCommand.ThrownExceptions)
				.Merge(LoadCommand.ThrownExceptions)
				.Merge(TestPasswordCommand.ThrownExceptions)
				.Merge(OpenFolderCommand.ThrownExceptions)
				.Merge(ImportColdcardCommand.ThrownExceptions)
				.Merge(EnumerateHardwareWalletsCommand.ThrownExceptions)
				.ObserveOn(RxApp.TaskpoolScheduler)
				.Subscribe(ex =>
				{
					NotificationHelpers.Error(ex.ToTypeMessageString());
					Logger.LogError(ex);
				});

			SetLoadButtonText();
		}

		public string UDevRulesLink => "https://github.com/bitcoin-core/HWI/tree/master/hwilib/udev";

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

		public ErrorDescriptors ValidatePassword() => PasswordHelper.ValidatePassword(Password);

		[ValidateMethod(nameof(ValidatePassword))]
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

		public void SetLoadButtonText()
		{
			var text = "Load Wallet";
			if (IsHardwareBusy)
			{
				text = "Waiting for Hardware Wallet...";
			}
			else if (IsBusy)
			{
				text = "Loading...";
			}
			else
			{
				// If the hardware wallet was not initialized, then make the button say Setup, not Load.
				// If pin is needed, then make the button say Send Pin instead.

				if (SelectedWallet?.HardwareWalletInfo != null)
				{
					if (!SelectedWallet.HardwareWalletInfo.IsInitialized())
					{
						text = "Setup Wallet";
					}

					if (SelectedWallet.HardwareWalletInfo.NeedsPinSent is true)
					{
						text = "Send PIN";
					}
				}
			}

			LoadButtonText = text;
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
					Logger.LogInfo(ex);
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

			TrySetWalletStates();

			if (!CanLoadWallet && Wallets.Count > 0)
			{
				NotificationHelpers.Warning("There is already an open wallet. Restart the application in order to open a different one.");
			}
		}

		private bool TrySetWalletStates()
		{
			try
			{
				if (SelectedWallet is null)
				{
					SelectedWallet = Wallets.FirstOrDefault();
				}

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
				}

				SetLoadButtonText();
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex);
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
				CanTestPassword = false;
				var password = Guard.Correct(Password); // Do not let whitespaces to the beginning and to the end.
				Password = ""; // Clear password field.

				var selectedWallet = SelectedWallet;
				if (selectedWallet is null)
				{
					NotificationHelpers.Warning("No wallet selected.");
					return null;
				}

				var walletName = selectedWallet.WalletName;
				if (isHardwareWallet)
				{
					var client = new HwiClient(Global.Network);

					if (selectedWallet.HardwareWalletInfo is null)
					{
						NotificationHelpers.Warning("No hardware wallet detected.");
						return null;
					}

					if (!selectedWallet.HardwareWalletInfo.IsInitialized())
					{
						try
						{
							IsHardwareBusy = true;
							MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.SettingUpHardwareWallet);

							// Setup may take a while for users to write down stuff.
							using (var ctsSetup = new CancellationTokenSource(TimeSpan.FromMinutes(21)))
							{
								// Trezor T doesn't require interactive mode.
								if (selectedWallet.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T
								|| selectedWallet.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T_Simulator)
								{
									await client.SetupAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, false, ctsSetup.Token);
								}
								else
								{
									await client.SetupAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, true, ctsSetup.Token);
								}
							}

							MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.ConnectingToHardwareWallet);
							await EnumerateHardwareWalletsAsync();
						}
						finally
						{
							IsHardwareBusy = false;
							MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusType.SettingUpHardwareWallet, StatusType.ConnectingToHardwareWallet);
						}

						return await LoadKeyManagerAsync(requirePassword, isHardwareWallet);
					}
					else if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
					{
						await PinPadViewModel.UnlockAsync(Global, selectedWallet.HardwareWalletInfo);

						var p = selectedWallet.HardwareWalletInfo.Path;
						var t = selectedWallet.HardwareWalletInfo.Model;
						await EnumerateHardwareWalletsAsync();
						selectedWallet = Wallets.FirstOrDefault(x => x.HardwareWalletInfo.Model == t && x.HardwareWalletInfo.Path == p);
						if (selectedWallet is null)
						{
							NotificationHelpers.Warning("Could not find the hardware wallet. Did you disconnect it?");
							return null;
						}
						else
						{
							SelectedWallet = selectedWallet;
						}

						if (!selectedWallet.HardwareWalletInfo.IsInitialized())
						{
							NotificationHelpers.Warning("Hardware wallet is not initialized.");
							return null;
						}

						if (selectedWallet.HardwareWalletInfo.NeedsPinSent is true)
						{
							NotificationHelpers.Warning("Hardware wallet needs a PIN to be sent.");
							return null;
						}
					}

					ExtPubKey extPubKey;
					var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
					try
					{
						MainWindowViewModel.Instance.StatusBar.TryAddStatus(StatusType.AcquiringXpubFromHardwareWallet);
						extPubKey = await client.GetXpubAsync(selectedWallet.HardwareWalletInfo.Model, selectedWallet.HardwareWalletInfo.Path, KeyManager.DefaultAccountKeyPath, cts.Token);
					}
					finally
					{
						cts?.Dispose();
						MainWindowViewModel.Instance.StatusBar.TryRemoveStatus(StatusType.AcquiringXpubFromHardwareWallet);
					}

					Logger.LogInfo("Hardware wallet was not used previously on this computer. Creating a new wallet file.");

					if (TryFindWalletByExtPubKey(extPubKey, out string wn))
					{
						walletName = wn;
					}
					else
					{
						walletName = Global.GetNextHardwareWalletName(selectedWallet.HardwareWalletInfo);
						var path = Global.GetWalletFullPath(walletName);

						// Get xpub should had triggered passphrase request, so the fingerprint should be available here.
						if (!selectedWallet.HardwareWalletInfo.Fingerprint.HasValue)
						{
							await EnumerateHardwareWalletsAsync();
							selectedWallet = Wallets.FirstOrDefault(x => x.HardwareWalletInfo.Model == selectedWallet.HardwareWalletInfo.Model && x.HardwareWalletInfo.Path == selectedWallet.HardwareWalletInfo.Path);
						}
						KeyManager.CreateNewHardwareWalletWatchOnly(selectedWallet.HardwareWalletInfo.Fingerprint.Value, extPubKey, path);
					}
				}

				var walletFullPath = Global.GetWalletFullPath(walletName);
				var walletBackupFullPath = Global.GetWalletBackupFullPath(walletName);
				if (!File.Exists(walletFullPath) && !File.Exists(walletBackupFullPath))
				{
					// The selected wallet is not available any more (someone deleted it?).
					OnCategorySelected();
					NotificationHelpers.Warning("The selected wallet and its backup do not exist, did you delete them?");
					return null;
				}

				KeyManager keyManager = Global.LoadKeyManager(walletFullPath, walletBackupFullPath);

				// Only check requirepassword here, because the above checks are applicable to loadwallet, too and we are using this function from load wallet.
				if (requirePassword)
				{
					if (PasswordHelper.TryPassword(keyManager, password, out string compatibilityPasswordUsed))
					{
						NotificationHelpers.Success("Correct password.");
						if (compatibilityPasswordUsed != null)
						{
							NotificationHelpers.Warning(PasswordHelper.CompatibilityPasswordWarnMessage);
						}

						keyManager.SetPasswordVerified();
					}
					else
					{
						NotificationHelpers.Error("Wrong password.");
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
					Logger.LogError(ex2);
				}

				// Initialization failed.
				NotificationHelpers.Error(ex.ToTypeMessageString());
				Logger.LogError(ex);

				return null;
			}
			finally
			{
				CanTestPassword = IsWalletSelected;
			}
		}

		private async Task EnumerateHardwareWalletsAsync()
		{
			var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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

				var keyManager = await LoadKeyManagerAsync(IsPasswordRequired, IsHardwareWallet);
				if (keyManager is null)
				{
					return;
				}

				try
				{
					await Task.Run(async () => await Global.InitializeWalletServiceAsync(keyManager));
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
					NotificationHelpers.Error(ex.ToTypeMessageString());
					if (!(ex is OperationCanceledException))
					{
						Logger.LogError(ex);
					}
					await Global.DisposeInWalletDependentServicesAsync();
				}
			}
			finally
			{
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
