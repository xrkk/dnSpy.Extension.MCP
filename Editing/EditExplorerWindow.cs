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
/// checkpoint lineage tree and checkpoint details.  The only action is a
/// guarded local cancel for an orphaned transaction (owner session gone);
/// there is deliberately no commit/restore/export UI, so nothing can bypass
/// the MCP gates.  All data arrives as immutable coordinator snapshots
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
		cancelButton = new Button { Content = "Cancel orphaned transaction (rollback)", Margin = new Thickness(8, 0, 8, 8), Padding = new Thickness(12, 4, 12, 4) };
		System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "McpEditCancelButton");
		cancelButton.Click += OnCancelClicked;
		var panel = new DockPanel();
		DockPanel.SetDock(stateLine, Dock.Top);
		DockPanel.SetDock(cancelLine, Dock.Top);
		DockPanel.SetDock(cancelButton, Dock.Bottom);
		panel.Children.Add(stateLine);
		panel.Children.Add(cancelLine);
		panel.Children.Add(cancelButton);
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
		// guarded cancel (adjudicated AUD-004): only an orphaned transaction
		// (owner session closed) may be rolled back from the UI; an active
		// owner keeps its transaction — the UI is a caretaker, not an override.
		var cancellable = snapshot.TransactionId != null && snapshot.OwnerClosed;
		cancelButton.IsEnabled = cancellable;
		cancelLine.Text = snapshot.TransactionId == null ? "No active transaction."
			: cancellable ? "Owner session is gone — this transaction can be rolled back locally."
			: "Transaction owned by an active MCP session (" + snapshot.OwnerTransport + ") — UI cancel disabled.";
		tree.Items.Clear();
		if (snapshot.TransactionId != null) {
			var transactionNode = new TreeViewItem { Header = "Transaction " + snapshot.TransactionId, IsExpanded = true };
			transactionNode.Items.Add(new TreeViewItem { Header = "work revision: " + snapshot.Revision });
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
			tree.Items.Add(transactionNode);
		}
		var lineageNode = new TreeViewItem { Header = "checkpoint lineages (" + snapshot.Lineages.Count + ")", IsExpanded = true };
		foreach (var lineage in snapshot.Lineages) {
			var lineageItem = new TreeViewItem { Header = lineage, IsExpanded = true };
			lineageNode.Items.Add(lineageItem);
		}
		tree.Items.Add(lineageNode);
	}

	void OnCancelClicked(object sender, RoutedEventArgs e) {
		coordinator.CancelOrphanedTransactionFromUi();
		Refresh();
	}
}
