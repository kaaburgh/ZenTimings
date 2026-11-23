using System;
using System.ComponentModel;
using System.Windows;
using ZenTimings.BiosOrigin;
using ZenTimings.ViewModels;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ZenTimings.Controls
{
    /// <summary>
    /// A label and its value as one element, so a timings panel is a single list of rows that can be
    /// reordered freely instead of two parallel label/value columns that must stay in step.
    /// <para>
    /// Rows line up within the nearest ancestor with <c>Grid.IsSharedSizeScope="True"</c>. Setting
    /// <see cref="UIElement.IsEnabled"/> greys out the whole row, label and value together, e.g. when
    /// the value isn't available.
    /// </para>
    /// </summary>
    public partial class TimingRow : UserControl
    {
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(TimingRow), new PropertyMetadata(null, OnRowDataChanged));

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(string), typeof(TimingRow), new PropertyMetadata(null, OnRowDataChanged));

        public static readonly DependencyProperty LabelToolTipProperty = DependencyProperty.Register(
            nameof(LabelToolTip), typeof(object), typeof(TimingRow), new PropertyMetadata(null));

        public static readonly DependencyProperty ValueToolTipProperty = DependencyProperty.Register(
            nameof(ValueToolTip), typeof(object), typeof(TimingRow), new PropertyMetadata(null));

        public static readonly DependencyProperty ValueMinWidthProperty = DependencyProperty.Register(
            nameof(ValueMinWidth), typeof(double), typeof(TimingRow), new PropertyMetadata(0.0, OnValueMinWidthChanged));

        public static readonly DependencyProperty OriginProperty = DependencyProperty.Register(
            nameof(Origin), typeof(SettingOriginRow), typeof(TimingRow), new PropertyMetadata(null, OnOriginChanged));

        public static readonly DependencyProperty OriginBadgeProperty = DependencyProperty.Register(
            nameof(OriginBadge), typeof(string), typeof(TimingRow), new PropertyMetadata(null));

        public static readonly DependencyProperty OriginSourceProperty = DependencyProperty.Register(
            nameof(OriginSource), typeof(string), typeof(TimingRow), new PropertyMetadata(null));

        public static readonly DependencyProperty OriginToolTipProperty = DependencyProperty.Register(
            nameof(OriginToolTip), typeof(string), typeof(TimingRow), new PropertyMetadata(null));

        public static readonly DependencyProperty OriginVisibilityProperty = DependencyProperty.Register(
            nameof(OriginVisibility), typeof(Visibility), typeof(TimingRow),
            new PropertyMetadata(Visibility.Collapsed));

        private MainViewModel originViewModel;

        public TimingRow()
        {
            InitializeComponent();
            ValueColumn.MinWidth = ValueMinWidth;
            Loaded += (sender, args) => AttachOriginViewModel(DataContext as MainViewModel);
            Unloaded += (sender, args) => AttachOriginViewModel(null);
            DataContextChanged += (sender, args) =>
            {
                if (IsLoaded) AttachOriginViewModel(args.NewValue as MainViewModel);
            };
        }

        /// <summary>The saved BIOS setting associated with this row, if the verified layout applies.</summary>
        public SettingOriginRow Origin
        {
            get => (SettingOriginRow)GetValue(OriginProperty);
            set => SetValue(OriginProperty, value);
        }

        public string OriginBadge
        {
            get => (string)GetValue(OriginBadgeProperty);
            private set => SetValue(OriginBadgeProperty, value);
        }

        public string OriginSource
        {
            get => (string)GetValue(OriginSourceProperty);
            private set => SetValue(OriginSourceProperty, value);
        }

        public string OriginToolTip
        {
            get => (string)GetValue(OriginToolTipProperty);
            private set => SetValue(OriginToolTipProperty, value);
        }

        public Visibility OriginVisibility
        {
            get => (Visibility)GetValue(OriginVisibilityProperty);
            private set => SetValue(OriginVisibilityProperty, value);
        }

        private void AttachOriginViewModel(MainViewModel model)
        {
            if (!ReferenceEquals(originViewModel, model))
            {
                if (originViewModel != null)
                {
                    originViewModel.PropertyChanged -= OnOriginViewModelChanged;
                    originViewModel.Settings.PropertyChanged -= OnOriginSettingChanged;
                }
                originViewModel = model;
                if (originViewModel != null)
                {
                    originViewModel.PropertyChanged += OnOriginViewModelChanged;
                    originViewModel.Settings.PropertyChanged += OnOriginSettingChanged;
                }
            }
            RefreshOrigin();
        }

        private void OnOriginViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.SettingOrigins))
                RefreshOrigin();
            else if (e.PropertyName == nameof(MainViewModel.VddioReadingSource) ||
                     e.PropertyName == nameof(MainViewModel.VmiscReadingSource))
                RefreshOriginDisplay();
        }

        private void OnOriginSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "ShowSettingOrigin")
                RefreshOrigin();
        }

        private static void OnRowDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((TimingRow)sender).RefreshOrigin();

        private static void OnOriginChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((TimingRow)sender).RefreshOriginDisplay();

        private void RefreshOrigin()
        {
            SettingOriginReport report = originViewModel?.SettingOrigins;
            if (report == null || !report.IsMatched || !originViewModel.Settings.ShowSettingOrigin)
            {
                Origin = null;
                RefreshOriginDisplay();
                return;
            }

            Binding valueBinding = BindingOperations.GetBinding(this, ValueProperty);
            Origin = report.FindByBindingPath(valueBinding?.Path?.Path, Label);
            RefreshOriginDisplay();
        }

        private void RefreshOriginDisplay()
        {
            if (Origin == null || originViewModel == null || !originViewModel.Settings.ShowSettingOrigin)
            {
                OriginBadge = null;
                OriginSource = null;
                OriginToolTip = null;
                OriginVisibility = Visibility.Collapsed;
                return;
            }

            // A raw setting from NVRAM is only labelled different if the displayed running value is
            // genuinely comparable. No mismatch for impedances, enumerations or derived timings.
            double mclk = originViewModel.PowerTable?.MCLK ?? 0;
            bool mismatch = Origin.DiffersFrom(Value, mclk) == true;
            OriginBadge = Origin.Badge + (mismatch ? " ≠" : "");
            OriginSource = Origin.Source;
            string source = null;
            if (Origin.Row == "MEM VDD" || Origin.Row == "MEM VDDQ" || Origin.Row == "MEM VPP")
                source = originViewModel.PmicData?.IsValid == true
                    ? "DIMM PMIC ADC reading" : "ACPI AOD region (not a PMIC measurement)";
            else if (Origin.Row == "CPU VDDIO")
                source = originViewModel.VddioReadingSource;
            else if (Origin.Row == "VDD MISC")
                source = originViewModel.VmiscReadingSource;
            OriginToolTip = Origin.Tooltip(Value, mclk, source);
            OriginVisibility = Visibility.Visible;
        }

        /// <summary>Row caption. A string so bindings to it may use StringFormat.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        /// <summary>Displayed value. A string so bindings to it may use StringFormat; "N/A" renders greyed out.</summary>
        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public object LabelToolTip
        {
            get => GetValue(LabelToolTipProperty);
            set => SetValue(LabelToolTipProperty, value);
        }

        public object ValueToolTip
        {
            get => GetValue(ValueToolTipProperty);
            set => SetValue(ValueToolTipProperty, value);
        }

        /// <summary>Minimum width of the value column, e.g. set for a whole group through an implicit style.</summary>
        public double ValueMinWidth
        {
            get => (double)GetValue(ValueMinWidthProperty);
            set => SetValue(ValueMinWidthProperty, value);
        }

        // A ColumnDefinition isn't in the visual tree, so it can't reach the row through RelativeSource.
        private static void OnValueMinWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            TimingRow row = (TimingRow)d;
            if (row.ValueColumn != null)
                row.ValueColumn.MinWidth = (double)e.NewValue;
        }

        /// <summary>The TextBlock that renders <see cref="Value"/>, for callers that measure or recolour it.</summary>
        public TextBlock ValueTextBlock => ValueBlock;

        /// <summary>
        /// Path of the binding behind the text <paramref name="text"/> shows. For a row's value block that is
        /// the row's own <see cref="Value"/> binding (e.g. "Timings.CL"), not the internal link to the row;
        /// for any other TextBlock it is its Text binding. Null when unbound.
        /// </summary>
        public static string GetDisplayedBindingPath(TextBlock text)
        {
            if (text == null)
                return null;

            for (DependencyObject parent = VisualTreeHelper.GetParent(text); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is TimingRow row)
                {
                    return ReferenceEquals(row.ValueBlock, text)
                        ? BindingOperations.GetBinding(row, ValueProperty)?.Path?.Path
                        : null;
                }
            }

            return BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path;
        }
    }
}
