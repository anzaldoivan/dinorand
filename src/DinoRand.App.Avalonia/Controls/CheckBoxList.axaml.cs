using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DinoRand.App
{
    /// <summary>
    /// Avalonia port of the WPF <c>CheckBoxList</c>: a list of named checkboxes with a context menu
    /// (Select all / Unselect all / Random). The list view is a <see cref="ListBox"/>; item state lives
    /// on an <see cref="INotifyPropertyChanged"/> item bound two-way to each <c>CheckBox</c>.
    /// </summary>
    public partial class CheckBoxList : UserControl
    {
        // Biorand used its own Rng/Shuffle helpers; DinoRand just uses System.Random.
        private readonly Random _random = new Random();
        private CheckBoxListItem[] _items = new CheckBoxListItem[0];
        private bool _suspendEvents;

        // Avalonia has no WPF-style RoutedEventHandler delegate; use EventHandler<RoutedEventArgs>.
        public event EventHandler<RoutedEventArgs> ItemValueChanged;

        public int Count => _items.Length;

        public CheckBoxList()
        {
            InitializeComponent();
        }

        public string[] Names
        {
            get => _items.Select(x => x.Text).ToArray();
            set
            {
                _items = value
                    .Select(x => new CheckBoxListItem(x, true))
                    .ToArray();
                list.ItemsSource = _items;
            }
        }

        public object[] ToolTips
        {
            get => _items.Select(x => x.Text).ToArray();
            set
            {
                for (var i = 0; i < _items.Length; i++)
                {
                    if (value.Length <= i)
                        break;

                    _items[i].ToolTip = value[i];
                }
            }
        }

        public bool[] Values
        {
            get => _items.Select(x => x.IsChecked).ToArray();
            set
            {
                for (var i = 0; i < Count; i++)
                {
                    SetItemChecked(i, value.Length > i && value[i]);
                }
            }
        }

        public void SetItemValues(bool[] values)
        {
            for (var i = 0; i < Count; i++)
            {
                var value = values.Length > i ? values[i] : false;
                SetItemChecked(i, value);
            }
        }

        public void SetItemChecked(int index, bool value)
        {
            if (index >= 0 && index < _items.Length)
            {
                _items[index].IsChecked = value;
            }
        }

        private void BulkModify(Action modifyLogic)
        {
            try
            {
                _suspendEvents = true;
                modifyLogic();
            }
            finally
            {
                _suspendEvents = false;
            }
            RaiseChangeEvent();
        }

        private void RaiseChangeEvent()
        {
            if (!_suspendEvents)
            {
                ItemValueChanged?.Invoke(this, new RoutedEventArgs());
            }
        }

        private void menuUnselectAll_Click(object sender, RoutedEventArgs e)
        {
            BulkModify(() =>
            {
                foreach (CheckBoxListItem item in _items)
                {
                    item.IsChecked = false;
                }
            });
        }

        private void menuSelectAll_Click(object sender, RoutedEventArgs e)
        {
            BulkModify(() =>
            {
                foreach (CheckBoxListItem item in _items)
                {
                    item.IsChecked = true;
                }
            });
        }

        private void menuRandom_Click(object sender, RoutedEventArgs e)
        {
            BulkModify(() =>
            {
                var items = _items;
                var numItems = items.Length;
                if (numItems == 0)
                    return;
                var numChecked = _random.Next(1, numItems);
                var checkedItems = items
                    .OrderBy(_ => _random.Next())
                    .Take(numChecked)
                    .ToArray();
                foreach (CheckBoxListItem item in items)
                {
                    item.IsChecked = checkedItems.Contains(item);
                }
            });
        }

        private void OnCheckBoxChanged(object sender, RoutedEventArgs e)
        {
            RaiseChangeEvent();
        }
    }

    public class CheckBoxListItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _text;
        private object _toolTip;
        private bool _isChecked;

        public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }
        }

        public object ToolTip
        {
            get => _toolTip;
            set
            {
                if (_toolTip != value)
                {
                    _toolTip = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
                }
            }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        public CheckBoxListItem()
        {
        }

        public CheckBoxListItem(string text, bool isChecked)
        {
            _text = text;
            _isChecked = isChecked;
        }
    }
}
