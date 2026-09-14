using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using dnSpy.Contracts.Menus;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P09 (ACC-018/RACC-018): a read-only dnSpy UI over the structured-edit
/// coordinator — current transaction, staged operations, diffs, risks, the
/// checkpoint lineage tree and checkpoint details.  The only action is a local
/// cancel of the current transaction (REQ-016): rollback while no operation or
/// commit is executing, orphaned or not; there is deliberately no
/// commit/restore/export UI, so nothing can bypass the MCP gates.  All data arrives as immutable coordinator snapshots
/// (<see cref="EditTransactionCoordinator.BuildExplorerSnapshot"/>); this
/// window never calls back into locking coordinator methods.
/// </summary>
[Export(typeof(IMenuItem))]
[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 90000, Header = "MCP Edit Explorer")]
internal sealed class EditExplorerMenuItem : MenuItemBase {
	readonly EditTransactionCoordinator coordinator;
	readonly EditExplorerWindow window;

	[ImportingConstructor]
	public EditExplorerMenuItem(EditTransactionCoordinator coordinator) {
		this.coordinator = coordinator;
		window = new EditExplorerWindow(coordinator);
	}

	public override void Execute(IMenuItemContext context) => EditExplorerWindow.ShowSingle(coordinator);
}

internal sealed class EditExplorerWindow : Window {
	static EditExplorerWindow? openInstance;
	readonly EditTransactionCoordinator coordinator;
	readonly DispatcherTimer timer;
	readonly TextBlock stateLine;
	readonly TextBlock cancelLine;
	readonly TreeView tree;
	readonly Button cancelButton;
	readonly TextBlock detailLine;
	string? lastCancelResult;

	public EditExplorerWindow(EditTransactionCoordinator coordinator) {
		this.coordinator = coordinator;
		Title = "MCP Edit Explorer";
		Width = 640; Height = 520;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		System.Windows.Automation.AutomationProperties.SetAutomationId(this, "McpEditExplorer");
		System.Windows.Automation.AutomationProperties.SetName(this, "MCP Edit Explorer");
		Topmost = true;
		stateLine = new TextBlock { TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(8, 8, 8, 0) };
		System.Windows.Automation.AutomationProperties.SetAutomationId(stateLine, "McpEditStateLine");
		cancelLine = new TextBlock { TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 0), FontWeight = FontWeights.Bold };
		System.Windows.Automation.AutomationProperties.SetAutomationId(cancelLine, "McpEditCancelLine");
		tree = new TreeView { Margin = new Thickness(8, 8, 8, 8) };
		System.Windows.Automation.AutomationProperties.SetAutomationId(tree, "McpEditTree");
		detailLine = new TextBlock { TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 0) };
		System.Windows.Automation.AutomationProperties.SetAutomationId(detailLine, "McpEditDetailLine");
		tree.SelectedItemChanged += (_, _) => ShowSelectedDetail();
		cancelButton = new Button { Content = "Cancel current transaction (rollback)", Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(12, 4, 12, 4) };
		System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "McpEditCancelButton");
		cancelButton.Click += OnCancelClicked;
		var panel = new DockPanel();
		DockPanel.SetDock(stateLine, Dock.Top);
		DockPanel.SetDock(cancelLine, Dock.Top);
		DockPanel.SetDock(cancelButton, Dock.Bottom);
		DockPanel.SetDock(detailLine, Dock.Bottom);
		panel.Children.Add(stateLine);
		panel.Children.Add(cancelLine);
		panel.Children.Add(cancelButton);
		panel.Children.Add(detailLine);
		panel.Children.Add(tree);
		Content = panel;
		timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) => Refresh();
		Loaded += (_, _) => { timer.Start(); Refresh(); };
		Closed += (_, _) => timer.Stop();
	}

	public static void ShowSingle(EditTransactionCoordinator coordinator) {
		if (openInstance != null) {
			openInstance.Activate();
			return;
		}
		var window = new EditExplorerWindow(coordinator);
		openInstance = window;
		window.Closed += (_, _) => openInstance = null;
		window.Show();
		window.Activate();
	}

	void Refresh() {
		var snapshot = coordinator.BuildExplorerSnapshot();
		stateLine.Text = "Coordinator state: " + snapshot.State + (snapshot.TransactionId == null ? ""
			: " | transaction " + snapshot.TransactionId + " rev " + snapshot.Revision + " (" + snapshot.OwnerTransport + " session)");
		// REQ-016 / CHK-001: local cancel of the CURRENT transaction is offered
		// whenever it is active and no operation/commit is executing; the owner
		// session being still connected no longer disables the button.  While an
		// operation or commit is in flight the button stays disabled (the
		// coordinator would reject the cancel with "busy").
		var cancellable = snapshot.TransactionId != null && snapshot.CanCancel;
		cancelButton.IsEnabled = cancellable;
		cancelLine.Text = snapshot.TransactionId == null ? "No active transaction."
			: cancellable ? (snapshot.OwnerClosed ? "Owner session is gone — this transaction can be rolled back locally."
				: "Current transaction (" + snapshot.OwnerTransport + " session) can be canceled locally — rollback only, no bypass.")
			: snapshot.CommitStarted ? "Commit is executing — local cancel unavailable."
			: "An edit operation is executing — local cancel unavailable.";
		if (lastCancelResult != null)
			cancelLine.Text = "last cancel result: " + lastCancelResult + " — " + cancelLine.Text;
		tree.Items.Clear();
		if (snapshot.TransactionId != null) {
			var transactionNode = new TreeViewItem { Header = "Transaction " + snapshot.TransactionId, IsExpanded = true };
			transactionNode.Items.Add(new TreeViewItem { Header = "work revision: " + snapshot.Revision });
			if (snapshot.OperationBusy)
				transactionNode.Items.Add(new TreeViewItem { Header = "Operation executing; transaction details updating." });
			else {
				var operationsNode = new TreeViewItem { Header = "staged operations (" + snapshot.Operations.Count + ")", IsExpanded = true };
				foreach (var operation in snapshot.Operations)
					operationsNode.Items.Add(new TreeViewItem { Header = operation });
				transactionNode.Items.Add(operationsNode);
				var diffsNode = new TreeViewItem { Header = "diffs (" + snapshot.Diffs.Count + ")" };
				foreach (var diff in snapshot.Diffs)
					diffsNode.Items.Add(new TreeViewItem { Header = diff });
				transactionNode.Items.Add(diffsNode);
				var risksNode = new TreeViewItem { Header = "risks (" + snapshot.Risks.Count + ")", IsExpanded = snapshot.Risks.Count != 0 };
				foreach (var risk in snapshot.Risks)
					risksNode.Items.Add(new TreeViewItem { Header = risk });
				transactionNode.Items.Add(risksNode);
				var validationNode = new TreeViewItem { Header = "validation (" + snapshot.Validation.Count + ")", IsExpanded = snapshot.Validation.Count != 0 };
				foreach (var row in snapshot.Validation)
					validationNode.Items.Add(new TreeViewItem { Header = row });
				transactionNode.Items.Add(validationNode);
			}
			tree.Items.Add(transactionNode);
		}
		var capacityNode = new TreeViewItem { Header = "capacity (current/maximum)", IsExpanded = false };
		foreach (var meter in snapshot.CapacityRows)
			capacityNode.Items.Add(new TreeViewItem { Header = meter });
		tree.Items.Add(capacityNode);
		var lineageNode = new TreeViewItem { Header = "checkpoint lineages (" + snapshot.Lineages.Count + ")", IsExpanded = true };
		foreach (var family in snapshot.Lineages.GroupBy(x => snapshot.LineageFamilies[x.Split(' ')[0]])) {
			var familyItem = new TreeViewItem { Header = "family " + family.Key, IsExpanded = true };
			foreach (var lineage in family) {
				var lineageId = lineage.Split(' ')[0];
				var lineageItem = new TreeViewItem { Header = lineage, IsExpanded = true };
				// CHK-002: one child node per checkpoint with parent/kind/image/semantic
				// rows — the branching history is browsable even when idle.
				var checkpointItems = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
				foreach (var checkpoint in snapshot.Checkpoints) {
					if (checkpoint.LineageId != lineageId) continue;
					var item = new TreeViewItem { Header = "checkpoint " + checkpoint.CheckpointId
						+ " parent " + (checkpoint.ParentCheckpointId == string.Empty ? "root" : checkpoint.ParentCheckpointId)
						+ " kind " + checkpoint.Kind
						+ " image " + checkpoint.ImageShaPrefix
						+ " semantic " + checkpoint.SemanticPrefix, Tag = checkpoint, IsExpanded = true };
					item.Items.Add(new TreeViewItem { Header = checkpoint.Detail });
					checkpointItems.Add(checkpoint.CheckpointId, item);
				}
				foreach (var checkpoint in snapshot.Checkpoints.Where(x => x.LineageId == lineageId)) {
					var parent = checkpoint.ParentCheckpointId == string.Empty ? lineageItem : checkpointItems[checkpoint.ParentCheckpointId];
					parent.Items.Add(checkpointItems[checkpoint.CheckpointId]);
				}
				familyItem.Items.Add(lineageItem);
			}
			lineageNode.Items.Add(familyItem);
		}
		tree.Items.Add(lineageNode);
	}

	void ShowSelectedDetail() {
		// CHK-014: the selected checkpoint's complete facts (full hashes,
		// sequence, review binding, validation summary, confirmed-risk set,
		// best-effort time) are shown in the detail line.
		if (tree.SelectedItem is TreeViewItem { Tag: EditTransactionCoordinator.ExplorerCheckpointRow row })
			detailLine.Text = row.Detail;
	}

	void OnCancelClicked(object sender, RoutedEventArgs e) {
		// REQ-016 / CHK-001: cancel the current transaction; the coordinator
		// reports whether it actually rolled back, was busy, or was already gone.
		lastCancelResult = coordinator.CancelTransactionFromUi();
		Refresh();
	}
}
