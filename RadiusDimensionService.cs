using System;
using System.Collections.Generic;
using Tekla.Structures.Drawing;

namespace RadiusDimensionMover
{
    public class MoveResult
    {
        public int TotalCount;
        public int MovedCount;
    }

    public class RadiusDimensionService
    {
        // Stos kolejnych "przesunięć" - każde kliknięcie "Przesuń" dokłada
        // na wierzch jeden zestaw (wymiar R -> Distance SPRZED tego kliknięcia).
        // Każde kliknięcie "Cofnij" zdejmuje jeden zestaw ze stosu i przywraca
        // te wartości, więc klikając "Cofnij" wielokrotnie, wracasz krok po
        // kroku aż do stanu sprzed pierwszego "Przesuń" (oryginału).
        private readonly Stack<List<(RadiusDimension dim, double previousDistance)>> _undoStack
            = new Stack<List<(RadiusDimension, double)>>();

        // Wartości Distance ustawione przez OSTATNIE udane "Przesuń" - używane
        // do wykrycia, czy użytkownik ręcznie przesunął któryś wymiar R na
        // rysunku (np. przeciągając go w Tekli) od tego momentu. Jeśli tak,
        // UI może z powrotem odblokować przycisk "Przesuń".
        private List<(RadiusDimension dim, double appliedDistance)> _lastAppliedMove
            = new List<(RadiusDimension, double)>();

        // Nazwa rysunku, na którym wykonano ostatnie udane "Przesuń" - jeśli
        // aktywny rysunek w Tekli się zmieni (np. otworzysz inny rysunek),
        // blokada przycisku "Przesuń" powinna zniknąć, bo "Cofnij" i tak nie
        // miałoby czego cofać na nowym rysunku.
        private string _lastMoveDrawingName;

        /// <summary>
        /// SPRAWDZONA W PRAKTYCE metoda: zwiększa Distance wszystkich
        /// wymiarów R na aktywnym rysunku o offsetMm - mm NA PAPIERZE,
        /// przeliczane przez skalę widoku (RadiusDimension.Distance jest w
        /// jednostkach MODELU, nie papieru - potwierdzone empirycznie na
        /// żywym rysunku "Einzelteil Blech" w skali 5:1, gdzie bez
        /// przeliczenia krok 100mm renderował się jako ~400-500mm na papierze).
        ///
        /// WAŻNE - dlaczego nie ma tu automatycznego omijania kolizji:
        /// Tekla Open API nie udostępnia ŻADNEGO sposobu odczytania ani
        /// przewidzenia rzeczywistej pozycji/kierunku tekstu wymiaru R.
        /// Sprawdzone empirycznie: ArcPoint1/2/3 są całkowicie STAŁE
        /// niezależnie od wartości Distance (logowano te same współrzędne
        /// przy Distance=3 i Distance=88,7) - to punkty definiujące geometrię
        /// samego łuku, niezwiązane z pozycją tekstu/leadera. Próby zgadywania
        /// kierunku geometrycznie (środek okręgu łuku, potem środek widoku)
        /// dawały błędne wyniki na żywych rysunkach. Sprawdzone też (i odrzucone,
        /// żeby nie próbować od nowa): RadiusDimension nie implementuje
        /// IAxisAlignedBoundingBox (sprawdzone refleksją po całym
        /// Tekla.Structures.Drawing.dll); rd.GetRelatedObjects() zwraca 0
        /// obiektów na żywym rysunku; rd.GetDimensionSet() rzuca wyjątek
        /// ("nieprawidłowa operacja") dla pojedynczego wymiaru R spoza
        /// łańcucha wymiarowego. Dlatego to Ty oceniasz wzrokowo w Tekli, czy
        /// krok wystarczył, i w razie potrzeby klikasz "Cofnij" + próbujesz
        /// ponownie z innym krokiem.
        /// </summary>
        public MoveResult MoveAllRadiusDimensionsOutward(double offsetMm, bool oppositeDirection, Action<string> log)
        {
            var thisMoveHistory = new List<(RadiusDimension, double)>();
            var appliedNow = new List<(RadiusDimension, double)>();

            var result = new MoveResult();

            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Nie można połączyć się z Tekla Structures. Upewnij się, że Tekla jest uruchomiona.");
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                throw new InvalidOperationException(
                    "Brak aktywnego rysunku. Otwórz rysunek pojedynczej części w edytorze rysunków Tekli.");
            }

            log("Aktywny rysunek: " + activeDrawing.Name);

            var sheet = activeDrawing.GetSheet();
            if (sheet == null)
            {
                throw new InvalidOperationException("Nie udało się pobrać arkusza rysunku (GetSheet).");
            }

            var radiusDimensions = new List<RadiusDimension>();

            DrawingObjectEnumerator objectEnum = sheet.GetAllObjects();
            while (objectEnum.MoveNext())
            {
                var rd = objectEnum.Current as RadiusDimension;
                if (rd != null)
                {
                    radiusDimensions.Add(rd);
                }
            }

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R.");

            // Skala widoku jest taka sama dla wymiarów z tego samego widoku -
            // liczymy raz na widok, nie raz na wymiar.
            var scaleCache = new Dictionary<ViewBase, double>();

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double currentDistance = rd.Distance;
                    double scale = GetViewScale(rd, scaleCache, log);

                    double magnitude = Math.Abs(currentDistance) + offsetMm / scale;
                    double newDistance = oppositeDirection ? -magnitude : magnitude;

                    log($"  Wymiar R: Distance {currentDistance:F2} -> {newDistance:F2} (jedn. modelu; skala widoku {scale:F3}; krok {offsetMm:F1}mm na papierze).");

                    thisMoveHistory.Add((rd, currentDistance));
                    rd.Distance = newDistance;

                    if (rd.Modify())
                    {
                        result.MovedCount++;
                        appliedNow.Add((rd, newDistance));
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Zapisano zmiany w rysunku (CommitChanges).");

            _undoStack.Push(thisMoveHistory);
            _lastAppliedMove = appliedNow;
            _lastMoveDrawingName = activeDrawing.Name;

            return result;
        }

        /// <summary>
        /// Zwraca skalę widoku (np. 5.0 dla rysunku szczegółowego "5:1"), w
        /// którym leży dany wymiar R - liczone raz na widok i zapamiętane w
        /// cache. Bezpieczny fallback = 1.0 (stare, "1mm = 1mm" zachowanie),
        /// jeśli nie da się odczytać widoku/skali.
        /// </summary>
        private static double GetViewScale(RadiusDimension rd, Dictionary<ViewBase, double> cache, Action<string> log)
        {
            ViewBase viewBase;
            try
            {
                viewBase = rd.GetView();
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się pobrać widoku wymiaru R - użyto skali 1.0. Błąd: " + ex.Message);
                return 1.0;
            }

            if (viewBase == null)
            {
                return 1.0;
            }

            if (cache.TryGetValue(viewBase, out double cachedScale))
            {
                return cachedScale;
            }

            double scale = 1.0;
            try
            {
                if (viewBase is View view)
                {
                    double s = view.Attributes.Scale;
                    if (s > 1e-6)
                    {
                        scale = s;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać skali widoku - użyto 1.0. Błąd: " + ex.Message);
            }

            cache[viewBase] = scale;
            return scale;
        }

        /// <summary>
        /// Cofa JEDEN krok ze stosu przesunięć (ostatnie kliknięcie "Przesuń"),
        /// przywracając zapamiętane wartości Distance sprzed tego kroku.
        /// Klikając "Cofnij" wielokrotnie, cofasz kolejne kroki jeden po
        /// drugim, aż do stanu sprzed pierwszego "Przesuń" w tej sesji
        /// (czyli do oryginału).
        /// </summary>
        public MoveResult UndoLastMove(Action<string> log)
        {
            var result = new MoveResult();

            if (_undoStack.Count == 0)
            {
                log("Brak zapisanej historii do cofnięcia (nie było wcześniejszego przesunięcia w tej sesji, albo już cofnięto wszystko do oryginału).");
                return result;
            }

            var lastMove = _undoStack.Pop();
            result.TotalCount = lastMove.Count;

            // Po cofnięciu nie ma już żadnego "ostatniego przesunięcia" do
            // porównywania - wyczyść bazę, żeby detekcja ręcznej zmiany nie
            // odpalała się na nieaktualnych danych.
            _lastAppliedMove = new List<(RadiusDimension, double)>();
            _lastMoveDrawingName = null;

            var drawingHandler = new DrawingHandler();
            if (!drawingHandler.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Nie można połączyć się z Tekla Structures. Upewnij się, że Tekla jest uruchomiona.");
            }

            Drawing activeDrawing = drawingHandler.GetActiveDrawing();
            if (activeDrawing == null)
            {
                throw new InvalidOperationException(
                    "Brak aktywnego rysunku. Otwórz rysunek pojedynczej części w edytorze rysunków Tekli.");
            }

            foreach (var entry in lastMove)
            {
                try
                {
                    entry.dim.Distance = entry.previousDistance;
                    if (entry.dim.Modify())
                    {
                        result.MovedCount++;
                    }
                    else
                    {
                        log("  Jeden wymiar R nie został przywrócony (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R przy cofaniu – błąd: " + ex.Message);
                }
            }

            activeDrawing.CommitChanges();
            log("Cofnięto jeden krok i zapisano rysunek (CommitChanges). Pozostało kroków do cofnięcia: " + _undoStack.Count);

            return result;
        }

        /// <summary>
        /// Sprawdza, czy stan się zmienił na tyle, że blokada "Przesuń"
        /// powinna zniknąć - albo dlatego, że ktoś ręcznie poprawił pozycję
        /// wymiaru na rysunku (Distance inne niż to, co ustawiliśmy), albo
        /// dlatego, że aktywny rysunek w Tekli jest teraz INNY niż ten, na
        /// którym wykonano ostatnie "Przesuń" (np. otwarto inny rysunek -
        /// "Cofnij" i tak nie miałoby tam czego cofać). Odczyty bezpośrednio
        /// z zapamiętanych obiektów RadiusDimension, bez ponownego
        /// wyszukiwania po arkuszu - jeśli którykolwiek rzuci wyjątkiem (np.
        /// usunięty, rysunek zamknięty), traktujemy to jako "coś się
        /// zmieniło" i wolimy bezpiecznie odblokować przycisk niż zablokować
        /// użytkownika.
        /// </summary>
        public bool HasAnyDimensionChangedSinceLastMove()
        {
            if (_lastAppliedMove.Count == 0)
            {
                return false;
            }

            try
            {
                var drawingHandler = new DrawingHandler();
                if (drawingHandler.GetConnectionStatus())
                {
                    Drawing activeDrawing = drawingHandler.GetActiveDrawing();
                    string currentDrawingName = activeDrawing?.Name;
                    if (currentDrawingName != _lastMoveDrawingName)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return true;
            }

            const double toleranceMm = 0.01;

            foreach (var entry in _lastAppliedMove)
            {
                try
                {
                    if (Math.Abs(entry.dim.Distance - entry.appliedDistance) > toleranceMm)
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }
    }
}
