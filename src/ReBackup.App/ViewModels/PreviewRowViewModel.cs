using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One visible row of the preview tree.</summary>
public sealed class PreviewRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly PreviewTreeViewModel _tree;
    private bool _isExpanded;

    public PreviewRowViewModel(PreviewTreeViewModel tree, EvaluatedNode node, long parentTotalSize, int depth,
        bool isExpanded)
    {
        _tree = tree;
        Node = node;
        Depth = depth;
        _isExpanded = isExpanded;
        PercentOfParent = parentTotalSize > 0 ? 100.0 * node.TotalSize / parentTotalSize : 0;
    }

    public EvaluatedNode Node { get; }
    public int Depth { get; }
    public double PercentOfParent { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree.OnExpandedChanged(this, value);
        }
    }

    public bool IsExpandable => Node.Children.Count > 0;
    public string Name => Node.Node.Name;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);
    public string SizeText => ByteSize.Format(Node.TotalSize);
    public string FilesText => Node.Node.IsDirectory ? Node.TotalFiles.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string PercentText => PercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";
    public string StatusText => Node.Status.ToString();
    public bool IsIgnored => Node.Status == IncludeStatus.Ignored;

    public string StatusDetail
    {
        get
        {
            var detail = Node.Status switch
            {
                IncludeStatus.Ignored when Node.IgnoredByParent =>
                    $"Ignored because a parent folder is ignored by \"{Node.Pattern?.Text}\" ({Node.Pattern?.Origin})",
                IncludeStatus.Ignored =>
                    $"Ignored by \"{Node.Pattern?.Text}\" ({Node.Pattern?.Origin})",
                IncludeStatus.Partial =>
                    $"Partly ignored: {ByteSize.Format(Node.IgnoredSize)} in {Node.IgnoredFiles:N0} files are skipped",
                _ when Node.Pattern is not null =>
                    $"Re-included by \"{Node.Pattern.Text}\" ({Node.Pattern.Origin})",
                _ => "Included",
            };
            return Node.Node.Error is null ? detail : $"{detail}\nCould not be read: {Node.Node.Error}";
        }
    }
}
