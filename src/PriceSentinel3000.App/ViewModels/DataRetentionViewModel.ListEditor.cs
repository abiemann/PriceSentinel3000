using System.Collections.Specialized;
using System.ComponentModel;
using PriceSentinel3000.Application.MarketDataLibrary;

namespace PriceSentinel3000.App.ViewModels;

public sealed partial class DataRetentionViewModel
{
    private readonly HashSet<DownloadMemberViewModel> _observedMembers = [];
    private int _listEditorUpdateDepth;
    private bool _showSaveListButton = true;
    private bool _hasSavedLists;
    private string _saveListButtonText = "SAVE LIST";

    public bool ShowSaveListButton => _showSaveListButton;
    public string SaveListButtonText => _saveListButtonText;
    public bool HasSavedLists => _hasSavedLists;

    private void InitializeListEditor()
    {
        Lists.CollectionChanged += OnSavedListsChanged;
        Members.CollectionChanged += OnListMembersChanged;
    }

    private void OnSavedListsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ListEditorChanged();

    private void OnListMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (DownloadMemberViewModel member in _observedMembers)
                member.PropertyChanged -= OnListMemberChanged;
            _observedMembers.Clear();
            foreach (DownloadMemberViewModel member in Members) ObserveListMember(member);
        }
        else
        {
            if (e.OldItems is not null)
                foreach (DownloadMemberViewModel member in e.OldItems)
                    if (!Members.Contains(member) && _observedMembers.Remove(member))
                        member.PropertyChanged -= OnListMemberChanged;
            if (e.NewItems is not null)
                foreach (DownloadMemberViewModel member in e.NewItems) ObserveListMember(member);
        }
        ListEditorChanged();
    }

    private void ObserveListMember(DownloadMemberViewModel member)
    {
        if (_observedMembers.Add(member)) member.PropertyChanged += OnListMemberChanged;
    }

    private void OnListMemberChanged(object? sender, PropertyChangedEventArgs e) => ListEditorChanged();

    private void UpdateListEditor(Action update)
    {
        _listEditorUpdateDepth++;
        try { update(); }
        finally { _listEditorUpdateDepth--; ListEditorChanged(); }
    }

    private void ListEditorChanged()
    {
        if (_listEditorUpdateDepth > 0 || _disposed) return;
        DownloadList? saved = Lists.FirstOrDefault(list => list.Id == SelectedList?.Id);
        bool show = saved is null || ListName.Trim() != saved.Name || ListEnabled != saved.IsEnabled ||
            _sourceListId != saved.SourceListId || _sourceListName != saved.SourceListName ||
            !Members.Select(member => member.ToMember()).SequenceEqual(saved.Members);
        string text = saved is null ? "SAVE LIST" : "UPDATE LIST";
        if (_saveListButtonText != text)
        {
            _saveListButtonText = text;
            Changed(nameof(SaveListButtonText));
        }
        if (_showSaveListButton != show)
        {
            _showSaveListButton = show;
            Changed(nameof(ShowSaveListButton));
        }
        if (_hasSavedLists != (Lists.Count > 0))
        {
            _hasSavedLists = Lists.Count > 0;
            Changed(nameof(HasSavedLists));
        }
        SaveListCommand?.RaiseCanExecuteChanged();
    }

    private void DisposeListEditor()
    {
        Lists.CollectionChanged -= OnSavedListsChanged;
        Members.CollectionChanged -= OnListMembersChanged;
        foreach (DownloadMemberViewModel member in _observedMembers)
            member.PropertyChanged -= OnListMemberChanged;
        _observedMembers.Clear();
    }
}
