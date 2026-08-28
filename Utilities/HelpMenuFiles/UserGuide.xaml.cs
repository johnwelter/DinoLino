using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace DinoLino
{
    public partial class UserGuideWindow : Window
    {
        private const string IdleStatus = "Click a megaphone to hear that section. Press Esc to stop.";

        /// <summary>A line made up only of dashes, which separates top-level sections.</summary>
        private static readonly Regex SectionDivider =
            new Regex(@"^[ \t]*-{8,}[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>A line like "----- Circular Arc -----", which marks a subheading.</summary>
        private static readonly Regex SubHeading =
            new Regex(@"^[ \t]*-{2,}[ \t]*(?<name>[^-].*?)[ \t]*-{2,}[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex RepeatedWhitespace =
            new Regex(@"[ \t]{2,}", RegexOptions.Compiled);

        /// <summary>
        /// Rewrites terms that read badly out loud. Applied in order, so longer
        /// patterns come before the single characters they contain. Add a line here
        /// whenever the synthesizer mangles something.
        /// </summary>
        private static readonly (string From, string To)[] SpokenSubstitutions =
        {
            ("Turn.Angles", "turning angles"),
            ("y=mx^2", "y equals m x squared"),
            ("^2", " squared"),
            ("π", " pi "),
            ("(bbox)", "(bounding box)"),
            ("EFA", "E F A"),
            ("(e.g.,", "(for example,"),
            ("e.g.,", "for example,"),
            ("i.e.,", "that is,"),
            ("and/or", "and or"),
            ("n-Point", "n point"),
            ("n-point", "n point"),
            ("2D", "two D"),
            ("3D", "three D"),
            ("Catmull-Rom", "Catmull Rom"),
            ("&", " and "),
            ("*", " times "),
            ("/", " over "),
            ("=", " equals "),
            ("+", " plus "),
            ("_", " "),
            ("\"", " "),
        };

        private readonly SpeechSynthesizer _synth = new SpeechSynthesizer();
        private readonly ObservableCollection<GuideSection> _sections = new ObservableCollection<GuideSection>();
        private readonly Queue<GuideSection> _readAllQueue = new Queue<GuideSection>();

        private Prompt _activePrompt;
        private GuideSection _speakingSection;
        private string _pendingVoice;
        private GuideSection _restartAfterVoiceChange;
        private bool _isReadingAll;
        private bool _suppressControlEvents = true;

        public UserGuideWindow()
        {
            InitializeComponent();

            _synth.SetOutputToDefaultAudioDevice();
            _synth.SpeakCompleted += OnSpeakCompleted;

            SectionList.ItemsSource = _sections;

            VoiceBox.ItemsSource = _synth.GetInstalledVoices()
                                         .Where(v => v.Enabled)
                                         .Select(v => v.VoiceInfo.Name)
                                         .ToList();
            VoiceBox.SelectedItem = _synth.Voice?.Name;
            RateSlider.Value = _synth.Rate;
            _suppressControlEvents = false;

            LoadGuide(ReadGuideText());
        }

        /// <summary>
        /// Splits the guide into sections and shows them. Call this instead of
        /// assigning the text to a single box if the guide is loaded elsewhere.
        /// </summary>
        public void LoadGuide(string guideText)
        {
            _sections.Clear();
            foreach (var section in ParseSections(guideText)) _sections.Add(section);

            if (_sections.Count > 0) _sections[0].IsExpanded = true;
            else StatusText.Text = "The guide text could not be loaded.";
        }

        // ----- Playback -----

        private void SpeakSection_Click(object sender, RoutedEventArgs e)
        {
            // Without this the click also toggles the expander it sits inside.
            e.Handled = true;

            if (!(sender is FrameworkElement element)) return;
            if (!(element.DataContext is GuideSection section)) return;

            var wasSpeaking = section.IsSpeaking;

            _isReadingAll = false;
            _readAllQueue.Clear();
            StopSpeaking();

            if (wasSpeaking)
            {
                ReturnToIdle();
                return;
            }

            section.IsExpanded = true;
            Speak(section);
        }

        private void ReadAll_Click(object sender, RoutedEventArgs e)
        {
            if (_speakingSection != null)
            {
                _isReadingAll = false;
                _readAllQueue.Clear();
                StopSpeaking();
                ReturnToIdle();
                return;
            }

            if (_sections.Count == 0) return;

            _readAllQueue.Clear();
            foreach (var section in _sections) _readAllQueue.Enqueue(section);

            _isReadingAll = true;
            SpeakNextInQueue();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (_speakingSection == null) return;

            if (_synth.State == SynthesizerState.Paused) _synth.Resume();
            else if (_synth.State == SynthesizerState.Speaking) _synth.Pause();

            UpdateTransportControls();
        }

        private void Speak(GuideSection section)
        {
            StopSpeaking();
            if (string.IsNullOrWhiteSpace(section.SpeechText)) return;

            _speakingSection = section;
            _activePrompt = new Prompt(section.SpeechText);
            section.IsSpeaking = true;

            StatusText.Text = "Reading: " + section.Title;
            UpdateTransportControls();

            _synth.SpeakAsync(_activePrompt);
        }

        private void SpeakNextInQueue()
        {
            if (_readAllQueue.Count == 0)
            {
                _isReadingAll = false;
                ReturnToIdle();
                return;
            }

            var next = _readAllQueue.Dequeue();
            (SectionList.ItemContainerGenerator.ContainerFromItem(next) as FrameworkElement)?.BringIntoView();
            Speak(next);
        }

        private void StopSpeaking()
        {
            if (_speakingSection != null) _speakingSection.IsSpeaking = false;
            _speakingSection = null;
            _activePrompt = null;

            if (_synth.State == SynthesizerState.Paused) _synth.Resume();
            _synth.SpeakAsyncCancelAll();
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            // This fires off the UI thread, and also fires for cancelled prompts.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // The synthesizer is idle now, so a waiting voice change is safe.
                if (_pendingVoice != null)
                {
                    ApplyPendingVoice();
                    return;
                }

                if (!ReferenceEquals(e.Prompt, _activePrompt)) return;

                if (_speakingSection != null) _speakingSection.IsSpeaking = false;
                _speakingSection = null;
                _activePrompt = null;

                if (_isReadingAll && _readAllQueue.Count > 0) SpeakNextInQueue();
                else ReturnToIdle();
            }));
        }

        private void ReturnToIdle()
        {
            _isReadingAll = false;
            StatusText.Text = IdleStatus;
            UpdateTransportControls();
        }

        private void UpdateTransportControls()
        {
            var speaking = _speakingSection != null;
            var showStop = speaking && _isReadingAll;

            ReadAllSpeakIcon.Visibility = showStop ? Visibility.Collapsed : Visibility.Visible;
            ReadAllWaveIcon.Visibility = showStop ? Visibility.Collapsed : Visibility.Visible;
            ReadAllStopIcon.Visibility = showStop ? Visibility.Visible : Visibility.Collapsed;
            ReadAllLabel.Text = showStop ? "Stop reading" : "Read the whole guide";

            var readAllLabel = showStop ? "Stop reading the guide" : "Read the whole guide aloud";
            ReadAllButton.ToolTip = readAllLabel;
            AutomationProperties.SetName(ReadAllButton, readAllLabel);

            var paused = _synth.State == SynthesizerState.Paused;
            PauseButton.IsEnabled = speaking;
            PauseIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
            ResumeIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;

            var pauseLabel = paused ? "Resume reading" : "Pause reading";
            PauseButton.ToolTip = pauseLabel;
            AutomationProperties.SetName(PauseButton, pauseLabel);
        }

        // ----- Toolbar and window plumbing -----

        private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressControlEvents) return;
            _synth.Rate = Math.Max(-10, Math.Min(10, (int)Math.Round(e.NewValue)));
        }

        private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressControlEvents) return;
            if (!(VoiceBox.SelectedItem is string voice)) return;

            // A switch is already waiting on the synthesizer, so just change its target.
            if (_pendingVoice != null)
            {
                _pendingVoice = voice;
                return;
            }

            if (_speakingSection == null)
            {
                SelectVoice(voice);
                return;
            }

            // Switching mid-sentence blocks the window until the section runs out,
            // so stop first and switch once the synthesizer has gone quiet.
            _pendingVoice = voice;
            _restartAfterVoiceChange = _speakingSection;
            StatusText.Text = "Switching voice…";
            StopSpeaking();
        }

        private void SelectVoice(string voice)
        {
            try
            {
                _synth.SelectVoice(voice);
            }
            catch (ArgumentException)
            {
                // The voice was uninstalled or disabled since the list was built.
            }
        }

        private void ApplyPendingVoice()
        {
            var voice = _pendingVoice;
            var restart = _restartAfterVoiceChange;

            _pendingVoice = null;
            _restartAfterVoiceChange = null;

            SelectVoice(voice);

            if (restart != null) Speak(restart);
            else ReturnToIdle();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _speakingSection == null) return;

            _isReadingAll = false;
            _readAllQueue.Clear();
            StopSpeaking();
            ReturnToIdle();
            e.Handled = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _synth.SpeakCompleted -= OnSpeakCompleted;

            try
            {
                if (_synth.State == SynthesizerState.Paused) _synth.Resume();
                _synth.SpeakAsyncCancelAll();
            }
            catch (ObjectDisposedException)
            {
            }

            _synth.Dispose();
        }

        /// <summary>
        /// Finds "User Guide.txt" beside the program, in the subfolder the build
        /// copies it into, or anywhere below the program's folder.
        /// </summary>
        private static string ReadGuideText()
        {
            const string fileName = "User Guide.txt";
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            var beside = Path.Combine(baseDirectory, fileName);
            if (File.Exists(beside)) return File.ReadAllText(beside);

            var copied = Path.Combine(baseDirectory, "Utilities", "HelpMenuFiles", fileName);
            if (File.Exists(copied)) return File.ReadAllText(copied);

            try
            {
                var found = Directory
                    .GetFiles(baseDirectory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found != null) return File.ReadAllText(found);
            }
            catch (UnauthorizedAccessException)
            {
            }

            return string.Empty;
        }

        private static IEnumerable<GuideSection> ParseSections(string guideText)
        {
            var sections = new List<GuideSection>();
            if (string.IsNullOrWhiteSpace(guideText)) return sections;

            var normalized = guideText.Replace("\r\n", "\n").Replace('\r', '\n');

            foreach (var block in SectionDivider.Split(normalized))
            {
                var lines = TrimBlankEdges(block.Split('\n'));
                if (lines.Count < 2) continue;

                var title = CleanTitle(lines[0]);
                var body = string.Join(Environment.NewLine, lines.GetRange(1, lines.Count - 1)).Trim();
                if (title.Length == 0 || body.Length == 0) continue;

                sections.Add(new GuideSection(title, body, ToSpeech(title, body)));
            }

            return sections;
        }

        private static string ToSpeech(string title, string body)
        {
            var builder = new StringBuilder();
            AppendSentence(builder, title);

            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    // A blank line becomes a paragraph break, which the synthesizer
                    // reads as a longer pause than a full stop.
                    builder.AppendLine();
                    continue;
                }

                var subHeading = SubHeading.Match(line);
                AppendSentence(builder, subHeading.Success ? subHeading.Groups["name"].Value : line);
            }

            return builder.ToString();
        }

        private static void AppendSentence(StringBuilder builder, string text)
        {
            var spoken = Speakable(text);
            if (spoken.Length == 0) return;

            builder.Append(spoken);
            if (".!?:".IndexOf(spoken[spoken.Length - 1]) < 0) builder.Append('.');
            builder.Append(' ');
        }

        private static string Speakable(string text)
        {
            var result = text.Trim().TrimEnd(':');
            foreach (var (from, to) in SpokenSubstitutions) result = result.Replace(from, to);

            return RepeatedWhitespace.Replace(result, " ").Trim();
        }

        /// <summary>Softens the all-caps headings so they are easier to scan on screen.</summary>
        private static string CleanTitle(string line)
        {
            var title = line.Trim().TrimEnd(':');

            var subHeading = SubHeading.Match(title);
            if (subHeading.Success) title = subHeading.Groups["name"].Value.Trim();

            var words = title.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].Length <= 2) continue;
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant();
            }

            return string.Join(" ", words);
        }

        private static List<string> TrimBlankEdges(string[] lines)
        {
            var trimmed = new List<string>(lines);

            while (trimmed.Count > 0 && trimmed[0].Trim().Length == 0) trimmed.RemoveAt(0);
            while (trimmed.Count > 0 && trimmed[trimmed.Count - 1].Trim().Length == 0)
                trimmed.RemoveAt(trimmed.Count - 1);

            return trimmed;
        }

        /// <summary>One block of the guide: a heading, its text, and a spoken rewrite.</summary>
        public sealed class GuideSection : INotifyPropertyChanged
        {
            private bool _isSpeaking;
            private bool _isExpanded;

            public GuideSection(string title, string body, string speechText)
            {
                Title = title;
                Body = body;
                SpeechText = speechText;
            }

            public string Title { get; }

            /// <summary>Text shown on screen, with the original spacing preserved.</summary>
            public string Body { get; }

            /// <summary>Text handed to the synthesizer, with symbols spelled out.</summary>
            public string SpeechText { get; }

            public bool IsSpeaking
            {
                get => _isSpeaking;
                set
                {
                    if (_isSpeaking == value) return;
                    _isSpeaking = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SpeakButtonLabel));
                }
            }

            public bool IsExpanded
            {
                get => _isExpanded;
                set
                {
                    if (_isExpanded == value) return;
                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }

            /// <summary>Tooltip and screen-reader name for this section's megaphone.</summary>
            public string SpeakButtonLabel =>
                IsSpeaking ? "Stop reading " + Title : "Read " + Title + " aloud";

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}