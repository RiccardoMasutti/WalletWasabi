using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using AvalonStudio.Extensibility;
using AvalonStudio.Shell;
using NBitcoin;
using ReactiveUI;
using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Gui.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Gui.Controls.WalletExplorer
{
	public class TransactionViewerViewModel : WalletActionViewModel
	{
		private CompositeDisposable Disposables { get; set; }

		private string _txId;
		private string _psbtJsonText;
		private string _psbtHexText;
		private string _psbtBase64Text;
		private byte[] _psbtBytes;
		public ReactiveCommand<Unit, Unit> ExportBinaryPsbt { get; set; }
		public ReactiveCommand<Unit, Unit> OpenTransactionBroadcaster { get; set; }

		public bool? IsLurkingWifeMode => Global.UiConfig.LurkingWifeMode;

		public string TxId
		{
			get => _txId;
			set => this.RaiseAndSetIfChanged(ref _txId, value);
		}

		public string PsbtJsonText
		{
			get => _psbtJsonText;
			set => this.RaiseAndSetIfChanged(ref _psbtJsonText, value);
		}

		public string TransactionHexText
		{
			get => _psbtHexText;
			set => this.RaiseAndSetIfChanged(ref _psbtHexText, value);
		}

		public string PsbtBase64Text
		{
			get => _psbtBase64Text;
			set => this.RaiseAndSetIfChanged(ref _psbtBase64Text, value);
		}

		public byte[] PsbtBytes
		{
			get => _psbtBytes;
			set => this.RaiseAndSetIfChanged(ref _psbtBytes, value);
		}

		public TransactionViewerViewModel(WalletViewModel walletViewModel) : base("Transaction", walletViewModel)
		{
			OpenTransactionBroadcaster = ReactiveCommand.Create(() => IoC.Get<IShell>().AddOrSelectDocument(() => new TransactionBroadcasterViewModel(Global)));
			ExportBinaryPsbt = ReactiveCommand.CreateFromTask(async () =>
			{
				var psbtExtension = "psbt";
				var sfd = new SaveFileDialog
				{
					DefaultExtension = psbtExtension,
					InitialFileName = TxId.Substring(0, 7),
					Title = "Export Binary PSBT"
				};

				if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				{
					var initialDirectory = Path.Combine("/media", Environment.UserName);
					if (!Directory.Exists(initialDirectory))
					{
						initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
					}
					sfd.Directory = initialDirectory;
				}
				else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				{
					sfd.Directory = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
				}

				var window = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime).MainWindow;
				string fileFullName = await sfd.ShowAsync(window, fallBack: true);
				if (!string.IsNullOrWhiteSpace(fileFullName))
				{
					var ext = Path.GetExtension(fileFullName);
					if (string.IsNullOrWhiteSpace(ext))
					{
						fileFullName = $"{fileFullName}.{psbtExtension}";
					}
					await File.WriteAllBytesAsync(fileFullName, PsbtBytes);
				}
			});

			Observable
				.Merge(ExportBinaryPsbt.ThrownExceptions)
				.Merge(OpenTransactionBroadcaster.ThrownExceptions)
				.ObserveOn(RxApp.TaskpoolScheduler)
				.Subscribe(ex =>
				{
					NotificationHelpers.Error(ex.ToTypeMessageString());
					Logger.LogError(ex);
				});
		}

		public override void OnOpen()
		{
			Disposables = Disposables is null ? new CompositeDisposable() : throw new NotSupportedException($"Cannot open {GetType().Name} before closing it.");

			base.OnOpen();

			Global.UiConfig.WhenAnyValue(x => x.LurkingWifeMode)
				.ObserveOn(RxApp.MainThreadScheduler)
				.Subscribe(_ =>
				{
					this.RaisePropertyChanged(nameof(IsLurkingWifeMode));
					this.RaisePropertyChanged(nameof(TxId));
					this.RaisePropertyChanged(nameof(PsbtJsonText));
					this.RaisePropertyChanged(nameof(TransactionHexText));
					this.RaisePropertyChanged(nameof(PsbtBase64Text));
				}).DisposeWith(Disposables);
		}

		public override bool OnClose()
		{
			Disposables?.Dispose();
			Disposables = null;

			return base.OnClose();
		}

		public void Update(BuildTransactionResult result)
		{
			try
			{
				TxId = result.Transaction.GetHash().ToString();
				PsbtJsonText = result.Psbt.ToString();
				TransactionHexText = result.Transaction.Transaction.ToHex();
				PsbtBase64Text = result.Psbt.ToBase64();
				PsbtBytes = result.Psbt.ToBytes();
			}
			catch (Exception ex)
			{
				NotificationHelpers.Error(ex.ToTypeMessageString());
				Logger.LogError(ex);
			}
		}
	}
}
