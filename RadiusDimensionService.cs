using System;
using System.Collections.Generic;
using System.Threading;
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
        // Stos kolejnych "przesunięć" - każde kliknięcie "Przesuń" dokłada na
        // wierzch jeden zestaw (wymiar R -> pełne Attributes + Distance
        // SPRZED tego kliknięcia). Każde kliknięcie "Cofnij" zdejmuje jeden
        // zestaw ze stosu i przywraca te wartości, więc klikając "Cofnij"
        // wielokrotnie, wracasz krok po kroku aż do stanu sprzed pierwszego
        // "Przesuń" (oryginału). Przywracamy CAŁE Attributes (nie tylko
        // Distance), bo tryb Free/Fixed to część Attributes.Placing.
        private readonly Stack<List<(RadiusDimension dim, RadiusDimensionAttributes previousAttributes, double previousDistance)>> _undoStack
            = new Stack<List<(RadiusDimension, RadiusDimensionAttributes, double)>>();

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

        // --- Parametry wyszukiwania wolnego miejsca (mm NA PAPIERZE,
        // przeliczane przez skalę widoku na jednostki modelu przed wysłaniem
        // do API - PlacingDistanceAttributes jest w tych samych jednostkach
        // co Distance, czyli w jednostkach modelu, nie papieru). ---
        private const double SearchMarginMm = 30.0;
        private const double MinimalDistanceMm = 15.0;
        private const double MaximalDistanceMm = 300.0;

        // Mały "neutralny" krok używany tylko po to, żeby wymusić świeże
        // przeliczenie przez Teklę przy przełączaniu Fixed -> Free (patrz
        // komentarz w AutoPlaceWithCollisionAvoidance).
        private const double ResetDistanceMm = 4.0;

        /// <summary>
        /// AUTOMATYCZNE rozstawianie wszystkich wymiarów R na aktywnym
        /// rysunku, unikając kolizji z innymi elementami - używając
        /// WBUDOWANEGO w Teklę silnika auto-rozstawiania wymiarów
        /// (RadiusDimensionAttributes.Placing = Placings.Free), a nie
        /// własnych zgadywanek.
        ///
        /// Znalezione po tym, jak wcześniejsze próby (patrz historia w git)
        /// zawiodły: RadiusDimension nie ma żadnej metody do odczytania
        /// swojej rzeczywistej pozycji na rysunku (ArcPoint1/2/3 są stałe,
        /// brak IAxisAlignedBoundingBox, GetRelatedObjects()/GetDimensionSet()
        /// nic nie dają) - więc nie da się zbudować własnego, niezawodnego
        /// wykrywania kolizji. Okazało się jednak, że Tekla ma do tego
        /// WŁASNY, wbudowany mechanizm (ten sam co przy StraightDimensionSet
        /// - "Placing: Free/Fixed"), który po prostu trzeba było znaleźć w
        /// atrybutach (odziedziczonych z DimensionSetBaseAttributes, nie
        /// zadeklarowanych bezpośrednio na RadiusDimensionAttributes - stąd
        /// wcześniej przeoczone).
        ///
        /// WAŻNE - dwuetapowość jest konieczna: samo ustawienie Placing=Free
        /// z nowymi parametrami wyszukiwania NIE wymusza ponownego
        /// przeliczenia, jeśli wymiar był już wcześniej w trybie Free
        /// (Tekla zdaje się cache'ować wynik). Trzeba najpierw przełączyć na
        /// Fixed z dowolnym Distance, zapisać, i DOPIERO wtedy przełączyć
        /// na Free ze świeżymi parametrami - potwierdzone empirycznie na
        /// żywym rysunku (dwa wymiary R, oba wylądowały w czystych,
        /// nienachodzących na siebie ani na inne elementy miejscach).
        ///
        /// WAŻNE - jednostki: PlacingDistanceAttributes (SearchMargin,
        /// MinimalDistance, MaximalDistance) są w jednostkach MODELU, tak
        /// samo jak zwykłe Distance - trzeba dzielić przez skalę widoku,
        /// inaczej (potwierdzone empirycznie) wymiar wyleci daleko poza
        /// widok przy widoku w powiększonej skali.
        /// </summary>
        public MoveResult AutoPlaceWithCollisionAvoidance(Action<string> log)
        {
            var thisMoveHistory = new List<(RadiusDimension, RadiusDimensionAttributes, double)>();
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
                if (objectEnum.Current is RadiusDimension rd)
                {
                    radiusDimensions.Add(rd);
                }
            }

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R.");

            var scaleCache = new Dictionary<ViewBase, double>();

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    RadiusDimensionAttributes originalAttrs = rd.Attributes;
                    double originalDistance = rd.Distance;
                    double scale = GetViewScale(rd, scaleCache, log);

                    // Krok 1: reset do Fixed - wymusza świeże przeliczenie
                    // przy następnym przełączeniu na Free (patrz dokumentacja
                    // metody wyżej).
                    var resetAttrs = rd.Attributes;
                    resetAttrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                        DimensionSetBaseAttributes.Placings.Fixed,
                        new PlacingDirectionAttributes(true, true),
                        new PlacingDistanceAttributes(2.0, ResetDistanceMm / scale));
                    rd.Attributes = resetAttrs;
                    rd.Distance = ResetDistanceMm / scale;
                    rd.Modify();
                    activeDrawing.CommitChanges();
                    Thread.Sleep(300);

                    // Krok 2: przełącz na Free z realnymi parametrami
                    // wyszukiwania (mm na papierze -> jednostki modelu).
                    var freeAttrs = rd.Attributes;
                    freeAttrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                        DimensionSetBaseAttributes.Placings.Free,
                        new PlacingDirectionAttributes(true, true),
                        new PlacingDistanceAttributes(SearchMarginMm / scale, MinimalDistanceMm / scale, MaximalDistanceMm / scale));
                    rd.Attributes = freeAttrs;

                    bool modifyResult = rd.Modify();
                    activeDrawing.CommitChanges();
                    Thread.Sleep(300);

                    if (modifyResult)
                    {
                        result.MovedCount++;
                        thisMoveHistory.Add((rd, originalAttrs, originalDistance));
                        appliedNow.Add((rd, rd.Distance));
                        log("  Wymiar R rozstawiony automatycznie (Tekla Placing=Free, margines " + SearchMarginMm + "mm, zakres " + MinimalDistanceMm + "-" + MaximalDistanceMm + "mm na papierze).");
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

            _undoStack.Push(thisMoveHistory);
            _lastAppliedMove = appliedNow;
            if (radiusDimensions.Count > 0)
            {
                _lastMoveDrawingName = activeDrawing.Name;
            }

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
        /// przywracając zapamiętane Attributes (w tym Placing) i Distance
        /// sprzed tego kroku. Klikając "Cofnij" wielokrotnie, cofasz kolejne
        /// kroki jeden po drugim, aż do stanu sprzed pierwszego "Przesuń" w
        /// tej sesji (czyli do oryginału).
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
                    entry.dim.Attributes = entry.previousAttributes;
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
