using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrbDock.Services;

namespace OrbDock.Views
{
    /// <summary>从开始菜单/桌面快捷方式里挑程序，支持多选。</summary>
    public partial class PickerWindow : Window
    {
        /// <summary>选中的快捷方式路径（可多个）。</summary>
        public List<string> SelectedPaths { get; private set; } = new List<string>();
        public List<string> SelectedNames { get; private set; } = new List<string>();

        private List<CatalogEntry> _all = new List<CatalogEntry>();
        private bool _updating;

        public PickerWindow()
        {
            InitializeComponent();

            TxtSearch.TextChanged += (s, e) => Filter();
            TxtSearch.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Accept();
                if (e.Key == Key.Down && LstCatalog.Items.Count > 0)
                {
                    LstCatalog.SelectedIndex = 0;
                    LstCatalog.Focus();
                }
            };
            BtnBrowse.Click += (s, e) => Browse();
            BtnOk.Click += (s, e) => Accept();
            BtnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            LstCatalog.MouseDoubleClick += (s, e) => Accept();
            LstCatalog.SelectionChanged += (s, e) => UpdateCount();
            BtnSelectAll.Click += (s, e) => SetAllSelection(true);
            BtnSelectNone.Click += (s, e) => SetAllSelection(false);
            BtnInvert.Click += (s, e) => InvertSelection();
            Loaded += (s, e) =>
            {
                UiUtil.BringToFront(this);
                TxtSearch.Focus();
            };
        }

        public void LoadCatalog(IEnumerable<string> existingTargets = null)
        {
            _all = AppCatalog.All();

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingTargets != null)
            {
                foreach (var t in existingTargets)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    existing.Add(t);
                    try { existing.Add(System.IO.Path.GetFullPath(t)); } catch { }
                }
            }

            foreach (var e in _all)
            {
                bool has = false;
                if (existing.Count > 0 && !string.IsNullOrWhiteSpace(e.Path))
                {
                    has = existing.Contains(e.Path);
                    if (!has)
                    {
                        try { has = existing.Contains(System.IO.Path.GetFullPath(e.Path)); } catch { }
                    }
                }
                e.Note = has ? "已在 Dock 中" : "";
            }

            Filter();
        }

        private void Filter()
        {
            var list = AppCatalog.Search(TxtSearch.Text);
            _updating = true;
            try
            {
                LstCatalog.ItemsSource = list;
                if (list.Count > 0) LstCatalog.SelectedIndex = 0;
            }
            finally { _updating = false; }
            UpdateCount();
        }

        private void UpdateCount()
        {
            int n = LstCatalog.SelectedItems.Count;
            int total = LstCatalog.Items.Count;
            if (n == 0)
            {
                TxCount.Text = string.Format("共 {0} 项", total);
                BtnOk.Content = "添加";
            }
            else
            {
                TxCount.Text = string.Format("已选 {0} 项 / 共 {1} 项　（Ctrl 点选、Shift 连选）", n, total);
                BtnOk.Content = string.Format("添加 {0} 项", n);
            }
        }

        private void SetAllSelection(bool on)
        {
            _updating = true;
            try
            {
                if (on) LstCatalog.SelectAll();
                else LstCatalog.UnselectAll();
            }
            finally { _updating = false; }
            UpdateCount();
        }

        private void InvertSelection()
        {
            _updating = true;
            try
            {
                var selected = new HashSet<object>(LstCatalog.SelectedItems.Cast<object>());
                var toSelect = LstCatalog.Items.Cast<object>().Where(o => !selected.Contains(o)).ToList();
                LstCatalog.UnselectAll();
                foreach (var o in toSelect) LstCatalog.SelectedItems.Add(o);
            }
            finally { _updating = false; }
            UpdateCount();
        }

        private void Browse()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Filter = "程序 (*.exe)|*.exe|快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*";
                dlg.Title = "选择要添加的程序（可按住 Ctrl / Shift 多选）";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    foreach (var f in dlg.FileNames)
                    {
                        SelectedPaths.Add(f);
                        SelectedNames.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                    }
                    DialogResult = true;
                    Close();
                }
            }
        }

        private void Accept()
        {
            var picked = LstCatalog.SelectedItems.Cast<CatalogEntry>().ToList();

            if (picked.Count == 0)
            {
                // 没勾选任何项时，允许把搜索框里输入的完整路径直接加进来
                var typed = TxtSearch.Text.Trim();
                if (!string.IsNullOrWhiteSpace(typed) && System.IO.File.Exists(typed))
                {
                    SelectedPaths.Add(typed);
                    SelectedNames.Add(System.IO.Path.GetFileNameWithoutExtension(typed));
                    DialogResult = true;
                    Close();
                }
                return;
            }

            foreach (var e in picked)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                SelectedPaths.Add(e.Path);
                SelectedNames.Add(string.IsNullOrWhiteSpace(e.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(e.Path)
                    : e.Name);
            }

            if (SelectedPaths.Count == 0) return;
            DialogResult = true;
            Close();
        }
    }
}
