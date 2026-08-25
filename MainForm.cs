using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RadiusDimensionMover
{
    public class MainForm : Form
    {
        // WAŻNE: jeden trwały obiekt serwisu przez cały czas życia okna,
        // żeby historia do "Cofnij" przetrwała między kliknięciami przycisków.
        private readonly RadiusDimensionService _service = new RadiusDimensionService();

        // Kierunek jest jawnym stanem, zmienianym WYŁĄCZNIE przez kliknięcie
        // przycisku "Odwróć kierunek". Kliknięcie tego przycisku NIE przesuwa
        // żadnych wymiarów - tylko zmienia flagę, użytą przy następnym
        // kliknięciu "Przesuń".
        private bool _oppositeDirection = false;

        // Blokada przycisku "Przesuń" po udanym przesunięciu - dopóki nie
        // klikniesz "Cofnij", kolejne kliknięcia "Przesuń" nic nie robią.
        // Chroni to przed sytuacją, gdy Tekla "zawiesza się" na chwilę,
        // użytkownik klika kilka razy myśląc że nic się nie stało, i wymiar
        // wylatuje daleko poza rysunek zamiast przesunąć się raz o krok.
        private bool _canRun = true;

        // Ścieżka do pliku logu tej sesji - zapisywana automatycznie, żeby
        // nie trzeba było ręcznie kopiować zawartości okna logu przy
        // zgłaszaniu problemu. Jeden plik na uruchomienie programu, w
        // podfolderze "logs" obok pliku .exe.
        private readonly string _logFilePath;

        private NumericUpDown _offsetInput;
        private Label _offsetLabel;
        private CheckBox _advancedCheckBox;
        private Button _toggleDirectionButton;
        private Button _runButton;
        private Button _undoButton;
        private Label _directionLabel;
        private TextBox _logBox;
        private Label _statusLabel;

        public MainForm()
        {
            _logFilePath = InitLogFile();

            Text = "Radius Dimension Mover – Tekla 2025";
            Width = 520;
            Height = 540;
            StartPosition = FormStartPosition.CenterScreen;

            // --- Wiersz 1: aktualny kierunek + przycisk odwracający ---
            _directionLabel = new Label
            {
                Left = 15,
                Top = 18,
                Width = 300,
                Height = 20,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            UpdateDirectionLabel();

            _toggleDirectionButton = new Button
            {
                Text = "Odwróć kierunek",
                Left = 330,
                Top = 12,
                Width = 155,
                Height = 32
            };
            _toggleDirectionButton.Click += ToggleDirectionButton_Click;

            // --- Wiersz 2: tryb zaawansowany (ręczny krok) - domyślnie wyłączony,
            // program sam wtedy dobiera odległość i unika kolizji ---
            _advancedCheckBox = new CheckBox
            {
                Text = "Zaawansowane: własny krok",
                Left = 15,
                Top = 60,
                Width = 220,
                Checked = false
            };
            _advancedCheckBox.CheckedChanged += AdvancedCheckBox_CheckedChanged;

            _offsetLabel = new Label
            {
                Text = "Krok [mm]:",
                Left = 250,
                Top = 62,
                Width = 70,
                Enabled = false
            };

            _offsetInput = new NumericUpDown
            {
                Left = 325,
                Top = 58,
                Width = 80,
                Minimum = 1,
                Maximum = 5000,
                // Podniesiony domyślny krok (było 20) - na podstawie testów
                // na żywych rysunkach (Einzelteil Träger) 15-20mm często
                // nie wystarczało, żeby ominąć sąsiednie wymiary/teksty.
                Value = 40,
                Enabled = false
            };

            // --- Wiersz 3: Przesuń - domyślnie auto (sam dobiera odległość,
            // omija kolizje); przy zaznaczonym checkboxie używa Krok [mm] ---
            _runButton = new Button
            {
                Text = "Przesuń wszystkie wymiary R (auto)",
                Left = 15,
                Top = 100,
                Width = 470,
                Height = 35
            };
            _runButton.Click += RunButton_Click;

            // --- Wiersz 4: Cofnij ---
            _undoButton = new Button
            {
                Text = "Cofnij",
                Left = 15,
                Top = 140,
                Width = 470,
                Height = 32
            };
            _undoButton.Click += UndoButton_Click;

            _statusLabel = new Label
            {
                Left = 15,
                Top = 182,
                Width = 470,
                Height = 20,
                ForeColor = Color.DarkSlateGray
            };

            _logBox = new TextBox
            {
                Left = 15,
                Top = 207,
                Width = 470,
                Height = 260,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9)
            };

            Controls.Add(_directionLabel);
            Controls.Add(_toggleDirectionButton);
            Controls.Add(_advancedCheckBox);
            Controls.Add(_offsetLabel);
            Controls.Add(_offsetInput);
            Controls.Add(_runButton);
            Controls.Add(_undoButton);
            Controls.Add(_statusLabel);
            Controls.Add(_logBox);

            // Naturalny moment, żeby sprawdzić, czy ktoś ręcznie poprawił
            // wymiar w Tekli: użytkownik musi kliknąć z powrotem na to okno,
            // żeby móc znów użyć "Przesuń" - to właśnie odpala Activated.
            Activated += MainForm_Activated;

            Log($"===== Start sesji {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            Log(_logFilePath != null
                ? "Log tej sesji zapisywany do pliku: " + _logFilePath
                : "UWAGA: nie udało się utworzyć pliku logu - log dostępny tylko w tym oknie.");
        }

        private void MainForm_Activated(object sender, EventArgs e)
        {
            if (_canRun)
            {
                // Już odblokowane - nie ma sensu odpytywać Tekli.
                return;
            }

            try
            {
                if (_service.HasAnyDimensionChangedSinceLastMove())
                {
                    _canRun = true;
                    _runButton.Enabled = true;
                    _statusLabel.Text = "Wykryto ręczną zmianę wymiaru na rysunku – przycisk Przesuń odblokowany.";
                }
            }
            catch
            {
                // Ciche pominięcie - nie chcemy wyskakujących błędów przy
                // zwykłym przełączeniu się z powrotem na to okno.
            }
        }

        private void AdvancedCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            bool advanced = _advancedCheckBox.Checked;
            _offsetLabel.Enabled = advanced;
            _offsetInput.Enabled = advanced;
            _runButton.Text = advanced
                ? "Przesuń wszystkie wymiary R (+krok)"
                : "Przesuń wszystkie wymiary R (auto)";
        }

        private void ToggleDirectionButton_Click(object sender, EventArgs e)
        {
            // Samo kliknięcie TYLKO zmienia stan - żadnego wywołania do Tekli,
            // żadnego przesunięcia. Efekt będzie widoczny dopiero po
            // kolejnym kliknięciu "Przesuń".
            _oppositeDirection = !_oppositeDirection;
            UpdateDirectionLabel();
        }

        private void UpdateDirectionLabel()
        {
            _directionLabel.Text = _oppositeDirection
                ? "Kierunek: PRZECIWNY (przez środek)"
                : "Kierunek: normalny (zgodnie ze strzałką)";
            _directionLabel.ForeColor = _oppositeDirection ? Color.DarkOrange : Color.DarkSlateGray;
        }

        private void RunButton_Click(object sender, EventArgs e)
        {
            if (!_canRun)
            {
                // Zabezpieczenie na wypadek, gdyby kliknięcie mimo wszystko
                // dotarło (np. kolejka komunikatów) mimo że przycisk powinien
                // być zablokowany - nic nie rób, nie przesuwaj drugi raz.
                return;
            }

            _logBox.Clear();
            bool advancedForHeader = _advancedCheckBox.Checked;
            Log($"===== {DateTime.Now:HH:mm:ss} PRZESUŃ - tryb: " +
                (advancedForHeader ? $"zaawansowane (krok={_offsetInput.Value}mm)" : "auto") +
                $", kierunek: {(_oppositeDirection ? "przeciwny" : "normalny")} =====");
            SetButtonsEnabled(false);
            _statusLabel.Text = "Przetwarzanie...";

            try
            {
                bool advanced = _advancedCheckBox.Checked;
                MoveResult result;

                if (advanced)
                {
                    double offsetMm = (double)_offsetInput.Value;

                    if (_service.WouldManualMoveCollide(offsetMm, _oppositeDirection))
                    {
                        bool proceed = ShowCollisionConfirm(
                            "Przy wpisanym kroku co najmniej jeden tekst wymiaru R będzie nachodził na inny " +
                            "element rysunku (inny tekst albo inny wymiar R).\n\n" +
                            "Kontynuować mimo to, czy anulować i np. zmniejszyć krok / użyć trybu auto?");

                        if (!proceed)
                        {
                            _statusLabel.Text = "Anulowano - kolizja przy wpisanym kroku.";
                            return;
                        }
                    }

                    result = _service.MoveAllRadiusDimensionsOutward(offsetMm, _oppositeDirection, Log);
                }
                else
                {
                    result = _service.AutoPlaceRadiusDimensionsAvoidingText(_oppositeDirection, Log);
                }

                // Po udanym przesunięciu blokujemy "Przesuń", żeby kolejne
                // kliknięcia (np. z niecierpliwości, gdy Tekla chwilę nie
                // odpowiada) nie dokładały kroku jeszcze raz. Żeby przesunąć
                // dalej, trzeba świadomie kliknąć "Cofnij" i spróbować ponownie.
                _canRun = false;

                _statusLabel.Text = $"Gotowe. Przesunięto {result.MovedCount} z {result.TotalCount} wymiarów R. Żeby przesunąć ponownie, najpierw kliknij Cofnij.";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Błąd – zobacz log.";
                Log("BŁĄD: " + ex.Message);
                Log(ex.StackTrace);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        /// <summary>
        /// Modalne ostrzeżenie o kolizji z opcją "Anuluj" / "Kontynuuj mimo to".
        /// Enter/Esc domyślnie trafiają na "Anuluj" - to bezpieczniejsza
        /// domyślna opcja niż przypadkowe przebicie się przez ostrzeżenie.
        /// </summary>
        private bool ShowCollisionConfirm(string message)
        {
            using (var dialog = new Form())
            {
                dialog.Text = "Uwaga - możliwa kolizja";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Width = 420;
                dialog.Height = 210;

                var label = new Label
                {
                    Text = message,
                    Left = 15,
                    Top = 15,
                    Width = 375,
                    Height = 110
                };

                var cancelButton = new Button
                {
                    Text = "Anuluj",
                    Left = 15,
                    Top = 135,
                    Width = 120,
                    Height = 32,
                    DialogResult = DialogResult.Cancel
                };

                var continueButton = new Button
                {
                    Text = "Kontynuuj mimo to",
                    Left = 265,
                    Top = 135,
                    Width = 125,
                    Height = 32,
                    DialogResult = DialogResult.OK
                };

                dialog.Controls.Add(label);
                dialog.Controls.Add(cancelButton);
                dialog.Controls.Add(continueButton);
                dialog.AcceptButton = cancelButton;
                dialog.CancelButton = cancelButton;

                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private void UndoButton_Click(object sender, EventArgs e)
        {
            _logBox.Clear();
            Log($"===== {DateTime.Now:HH:mm:ss} COFNIJ =====");
            SetButtonsEnabled(false);
            _statusLabel.Text = "Cofanie...";

            try
            {
                var result = _service.UndoLastMove(Log);

                if (result.TotalCount == 0)
                {
                    _statusLabel.Text = "Brak historii do cofnięcia.";
                }
                else
                {
                    _statusLabel.Text = $"Cofnięto {result.MovedCount} z {result.TotalCount} wymiarów R.";
                }

                // Cofnięcie (nawet gdy nie było czego cofać) odblokowuje
                // "Przesuń" - wracamy do stanu, w którym jedno kliknięcie
                // = jedno przesunięcie.
                _canRun = true;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Błąd – zobacz log.";
                Log("BŁĄD: " + ex.Message);
                Log(ex.StackTrace);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _toggleDirectionButton.Enabled = enabled;
            _advancedCheckBox.Enabled = enabled;
            // "Przesuń" wraca do stanu aktywnego tylko jeśli nie jest
            // zablokowany przez _canRun (czyli dopóki nie kliknięto "Cofnij"
            // po ostatnim udanym przesunięciu).
            _runButton.Enabled = enabled && _canRun;
            _undoButton.Enabled = enabled;
        }

        /// <summary>
        /// Tworzy podfolder "logs" obok pliku .exe i zwraca ścieżkę do
        /// nowego pliku logu na tę sesję (jedno uruchomienie programu =
        /// jeden plik, wszystkie akcje dopisywane po kolei). Jeśli z
        /// jakiegoś powodu nie da się utworzyć folderu/pliku (np. brak
        /// uprawnień), program ma dalej działać - po prostu bez logu do pliku.
        /// </summary>
        private static string InitLogFile()
        {
            try
            {
                string logDir = Path.Combine(Application.StartupPath, "logs");
                Directory.CreateDirectory(logDir);
                return Path.Combine(logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
            catch
            {
                return null;
            }
        }

        private void Log(string message)
        {
            _logBox.AppendText(message + Environment.NewLine);

            if (_logFilePath == null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
            catch
            {
                // Błąd zapisu do pliku loga nie może przerwać działania
                // programu - to tylko dodatkowa wygoda, nie krytyczna funkcja.
            }
        }
    }
}
