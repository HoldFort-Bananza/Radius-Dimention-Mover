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

        // Analogiczny stos dla opisów (Mark, np. "1*Ø13") przesuwanych bliżej
        // razem z wymiarami R w tym samym kliknięciu "Przesuń" - zdejmowany
        // z "Cofnij" w parze z powyższym stosem.
        private readonly Stack<List<(Mark mark, Mark.MarkAttributes previousAttributes)>> _markUndoStack
            = new Stack<List<(Mark, Mark.MarkAttributes)>>();

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
        // komentarz w PlaceUsingFreeMode).
        private const double ResetDistanceMm = 4.0;

        // --- Parametry podejścia "wizualnego" (WindowCapture) używanego do
        // WYMUSZENIA, żeby wymiar R zawsze lądował NA ZEWNĄTRZ konturu
        // części, a nie w środku - patrz dokumentacja TryPlaceOutside(). ---

        // Odległość próbna (na papierze) używana do sprawdzenia, w którą
        // stronę (+/-) faktycznie wypada tekst wymiaru - musi być
        // wystarczająco duża, żeby jednoznacznie "wyjść" poza mały kontur,
        // ale nie tak duża, żeby wylecieć poza okno/arkusz.
        private const double ProbeDistanceMm = 70.0;

        // Malutkie "szturchnięcie" (Fixed, Distance dodatnie) używane
        // TYLKO po to, żeby wizualnie zlokalizować (przez różnicę zrzutów)
        // MIEJSCE na ekranie, w którym leży dany wymiar/narożnik - bo samo
        // RadiusDimension nie ma żadnej metody odczytu swojej pozycji.
        private const double AnchorProbeDistanceMm = 6.0;

        // Zakres i krok wyszukiwania finalnej, wolnej od kolizji odległości
        // wzdłuż JUŻ WYBRANEJ (wizualnie potwierdzonej) strony.
        private const double FinalMinDistanceMm = 60.0;
        private const double FinalMaxDistanceMm = 150.0;
        private const double FinalStepMm = 15.0;

        // Stały, uniwersalny odstęp ZA najdalej wykrytą linią/opisem
        // wymiarowym (zamiast "pierwszy wolny krok skanu", co zależało od
        // FinalStepMm) - żeby wynik był przewidywalny niezależnie od
        // konkretnego układu rysunku.
        private const double ClearanceBeyondLastLineMm = 25.0;

        // Rozmiar (px) kwadratu sprawdzanego pod kątem zajętości wokół
        // kandydującej pozycji tekstu oraz próg "to już realna treść (linia/
        // opis wymiarowy), nie szum tła" przy wyszukiwaniu najdalszej
        // zajętej pozycji.
        private const int OccupancyBoxSizePx = 36;
        private const double ContentPresentOccupancyThreshold = 0.05;

        // Minimalna liczba różniących się pikseli, żeby uznać zrzut "przed"
        // i "po" za realną, wiarygodną zmianę (a nie szum/nic się nie stało).
        private const int MinDiffPixelsForValidProbe = 8;

        // --- Parametry "dociągania" opisów (Mark, np. "1*Ø13") bliżej -
        // domyślnie mają MaximalDistance=0 (bez limitu), przez co potrafią
        // wylądować bardzo daleko od tego, co opisują. Ograniczamy zakres,
        // żeby zostały blisko, ale wciąż z dala od kolizji. ---
        private const double MarkSearchMarginMm = 15.0;
        private const double MarkMinimalDistanceMm = 10.0;
        private const double MarkMaximalDistanceMm = 60.0;

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
            var marks = new List<Mark>();
            DrawingObjectEnumerator objectEnum = sheet.GetAllObjects();
            while (objectEnum.MoveNext())
            {
                var current = objectEnum.Current;
                if (current is RadiusDimension rd)
                {
                    radiusDimensions.Add(rd);
                }
                else if (current is Mark mark)
                {
                    marks.Add(mark);
                }
            }

            result.TotalCount = radiusDimensions.Count;
            log("Znaleziono " + result.TotalCount + " wymiar(ów) R oraz " + marks.Count + " opis(ów) (Mark).");

            var scaleCache = new Dictionary<ViewBase, double>();

            // Najpierw dociągamy opisy (np. "1*Ø13") bliżej - zanim wymiary R
            // szukają wolnego miejsca, żeby nie musiały omijać opisów
            // wyrzuconych daleko poza to, co realnie opisują.
            var markMoveHistory = TightenMarks(marks, activeDrawing, scaleCache, log);
            _markUndoStack.Push(markMoveHistory);

            // --- Przygotowanie "wizyjnego" wymuszania strony (poza kontur
            // części, nigdy do środka) - patrz TryPlaceOutside(). Jeśli
            // cokolwiek się nie uda (okno Tekli nie znalezione, kontur nie
            // wykryty), po prostu NIE używamy tej ścieżki i każdy wymiar R
            // spada do starego, sprawdzonego trybu Free - program ma zawsze
            // coś zrobić, nigdy nie ma się wywalić z powodu samej wizji. ---
            IntPtr teklaHwnd = IntPtr.Zero;
            System.Drawing.Bitmap reference = null;
            bool visionAvailable = false;
            try
            {
                teklaHwnd = WindowCapture.FindTeklaWindow();
                if (teklaHwnd != IntPtr.Zero)
                {
                    reference = WindowCapture.CaptureWindow(teklaHwnd);
                    visionAvailable = true;
                }

                if (!visionAvailable)
                {
                    log("  [WIZJA] Niedostępna (okno Tekli nie znalezione) - używam wyłącznie trybu Free.");
                }
            }
            catch (Exception ex)
            {
                log("  [WIZJA] Błąd inicjalizacji - używam wyłącznie trybu Free. Błąd: " + ex.Message);
                visionAvailable = false;
            }

            try
            {
                foreach (var rd in radiusDimensions)
                {
                    try
                    {
                        RadiusDimensionAttributes originalAttrs = rd.Attributes;
                        double originalDistance = rd.Distance;
                        double scale = GetViewScale(rd, scaleCache, log);

                        bool placed = false;
                        if (visionAvailable)
                        {
                            placed = TryPlaceOutside(rd, activeDrawing, scale, teklaHwnd, ref reference, log);
                        }

                        if (!placed)
                        {
                            placed = PlaceUsingFreeMode(rd, activeDrawing, scale, log);
                            if (placed && visionAvailable)
                            {
                                // Odśwież referencyjny zrzut, żeby kolejne
                                // wymiary R wiedziały, że tu już coś jest -
                                // nawet jeśli ten konkretny trafił do trybu
                                // awaryjnego (Free), a nie wizyjnego.
                                try
                                {
                                    reference?.Dispose();
                                    reference = WindowCapture.CaptureWindow(teklaHwnd);
                                }
                                catch
                                {
                                    visionAvailable = false;
                                }
                            }
                        }

                        if (placed)
                        {
                            result.MovedCount++;
                            thisMoveHistory.Add((rd, originalAttrs, originalDistance));
                            appliedNow.Add((rd, rd.Distance));
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
            }
            finally
            {
                reference?.Dispose();
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
        /// Wymusza, żeby wymiar R wylądował NA ZEWNĄTRZ konturu części, a
        /// nie w środku - odpowiedź na to, że tryb Placing=Free (patrz
        /// PlaceUsingFreeMode) wybiera kąt/stronę wg czegoś ustalonego "na
        /// sztywno" per wymiar, czego NIE da się zmienić żadnym atrybutem
        /// API (sprawdzone empirycznie: PlacingDirectionAttributes.
        /// Positive/Negative nie ma żadnego wpływu na wybraną stronę - trzy
        /// niezależne testy dały identyczny wynik).
        ///
        /// PIERWSZE podejście (wykrywanie "białego konturu" na zrzucie
        /// ekranu, globalnie i lokalnie) ZAWIODŁO - linie/strzałki wymiarowe
        /// są rysowane tym samym prawie-białym kolorem co krawędzie części,
        /// więc nie da się ich odróżnić samą analizą koloru pikseli
        /// (potwierdzone empirycznie: wykryty "kontur" wychodził na niemal
        /// cały rysunek).
        ///
        /// Zamiast tego liczymy kierunek "na zewnątrz" WPROST Z GEOMETRII
        /// łuku: RadiusDimension.ArcPoint1/2/3 (w jednostkach modelu, ta
        /// sama przestrzeń co Distance) dają 3 punkty na okręgu, z których
        /// liczymy środek (circumcenter). Dla wypukłego zaokrąglenia
        /// narożnika blachy (typowy przypadek) środek okręgu leży PO
        /// STRONIE MATERIAŁU - więc kierunek "od środka, przez łuk, dalej
        /// na zewnątrz" jest kierunkiem "od materiału", niezależnie od
        /// jakiejkolwiek analizy pikseli. To jedyna naprawdę niezawodna
        /// dźwignia w tym całym probemie.
        ///
        /// Wizja (zrzut ekranu, odporny na zasłonięcia - WindowCapture) jest
        /// używana tylko w DWÓCH miejscach: (1) JEDNORAZOWO na wymiar, żeby
        /// sprawdzić, czy Tekla przyjęła znak "+" Distance jako ruch w
        /// policzonym kierunku "na zewnątrz", czy przeciwnie (Tekla może
        /// stosować dowolną wewnętrzną konwencję, której nie da się
        /// odgadnąć bez jednego rzeczywistego pomiaru), (2) do finalnego
        /// wyszukania wolnego (nienachodzącego na inne elementy) miejsca
        /// wzdłuż TEJ potwierdzonej strony.
        ///
        /// Zwraca false (bez wyjątku), jeśli z jakiegokolwiek powodu nie da
        /// się tego wiarygodnie ustalić (np. zdegenerowana geometria łuku,
        /// brak zauważalnej zmiany na zrzutach) - wywołujący ma wtedy spaść
        /// do PlaceUsingFreeMode jako bezpiecznego wariantu awaryjnego.
        /// </summary>
        private bool TryPlaceOutside(
            RadiusDimension rd, Drawing activeDrawing, double scale, IntPtr teklaHwnd,
            ref System.Drawing.Bitmap reference, Action<string> log)
        {
            System.Drawing.Bitmap beforeShot = reference;

            try
            {
                Tekla.Structures.Geometry3d.Point center;
                try
                {
                    center = CircumCenter(rd.ArcPoint1, rd.ArcPoint2, rd.ArcPoint3);
                }
                catch (Exception ex)
                {
                    log("  [WIZJA] Nie udało się policzyć środka łuku - pomijam wizualne wymuszanie strony. Błąd: " + ex.Message);
                    return false;
                }

                double outVx = rd.ArcPoint2.X - center.X;
                double outVy = rd.ArcPoint2.Y - center.Y;
                double outLen = Math.Sqrt(outVx * outVx + outVy * outVy);
                if (outLen < 1e-6)
                {
                    log("  [WIZJA] Zdegenerowana geometria łuku (promień ~0) - pomijam wizualne wymuszanie strony.");
                    return false;
                }
                outVx /= outLen;
                outVy /= outLen;

                // Oczekiwany kierunek "na zewnątrz" w pikselach: X bez
                // zmian, Y odwrócone (CAD: Y rośnie w górę; piksele obrazu:
                // Y rośnie w dół) - standardowy, nieobrócony widok 2D.
                double expectedPixelDx = outVx;
                double expectedPixelDy = -outVy;

                // Jedna próbna zmiana w stronę "+", żeby sprawdzić, czy
                // zgadza się z policzonym kierunkiem "na zewnątrz".
                var anchorProbe = ProbeSign(rd, activeDrawing, scale, +1, AnchorProbeDistanceMm, teklaHwnd, beforeShot);
                var posProbe = ProbeSign(rd, activeDrawing, scale, +1, ProbeDistanceMm, teklaHwnd, beforeShot);

                if (!anchorProbe.valid || !posProbe.valid)
                {
                    log("  [WIZJA] Brak wykrywalnej zmiany na zrzutach ekranu - pomijam wizualne wymuszanie strony.");
                    return false;
                }

                double observedDx = posProbe.cx - anchorProbe.cx;
                double observedDy = posProbe.cy - anchorProbe.cy;
                double dot = observedDx * expectedPixelDx + observedDy * expectedPixelDy;
                int sign = dot >= 0 ? +1 : -1;

                log("  [WIZJA] Kierunek na zewnątrz policzony ze środka łuku (ArcPoint1/2/3) - potwierdzona strona: " + (sign > 0 ? "+" : "-") + ".");

                // Skanujemy CAŁY zakres (zamiast zatrzymać się na pierwszym
                // wolnym miejscu) i lądujemy TUŻ ZA najdalszą wykrytą
                // "zajętością" (czyli na wysokości najdalszej istniejącej
                // linii/opisu wymiarowego w tym kierunku) - użytkownik chce
                // wymiar R wyrównany z resztą stosu wymiarów, a nie w
                // pierwszej wolnej szczelinie, która często jest tuż przy
                // części, przed jakąkolwiek inną linią wymiarową.
                var occupancies = new List<double>();
                double sheetLimitDistance = double.MaxValue;
                for (double d = FinalMinDistanceMm; d <= FinalMaxDistanceMm; d += FinalStepMm)
                {
                    SetFixedDistance(rd, activeDrawing, scale, sign * d);
                    Thread.Sleep(200);
                    using (var shot = WindowCapture.CaptureWindow(teklaHwnd))
                    {
                        var diff = WindowCapture.DiffCentroid(beforeShot, shot);
                        bool validDiff = diff.count >= MinDiffPixelsForValidProbe;
                        double occupancy = validDiff
                            ? WindowCapture.GetOccupancyFraction(beforeShot, diff.cx, diff.cy, OccupancyBoxSizePx)
                            : 0.0;
                        occupancies.Add(occupancy);

                        // Krawędź arkusza to TWARDY limit - nigdy jej nie
                        // przeskakujemy (w odróżnieniu od zwykłej treści).
                        if (validDiff && sheetLimitDistance == double.MaxValue
                            && WindowCapture.HasFrameOrGuideColor(beforeShot, diff.cx, diff.cy, OccupancyBoxSizePx))
                        {
                            sheetLimitDistance = d;
                            log("  [WIZJA] Skan " + d.ToString("0") + "mm: osiągnięto krawędź arkusza - dalej nie szukam.");
                            break;
                        }

                        log("  [WIZJA] Skan " + d.ToString("0") + "mm: zajętość=" + occupancy.ToString("0.00"));
                    }
                }

                int lastOccupiedIndex = occupancies.FindLastIndex(o => o > ContentPresentOccupancyThreshold);
                double finalDistance;
                if (lastOccupiedIndex >= 0)
                {
                    double lastOccupiedDistance = FinalMinDistanceMm + lastOccupiedIndex * FinalStepMm;
                    finalDistance = lastOccupiedDistance + ClearanceBeyondLastLineMm;
                }
                else
                {
                    finalDistance = FinalMinDistanceMm;
                }

                // Nie wychodź poza krawędź arkusza - cofnij się o jeden krok
                // przed nią, jeśli wyliczona odległość by ją przekroczyła.
                if (sheetLimitDistance != double.MaxValue)
                {
                    double maxAllowed = Math.Max(FinalMinDistanceMm, sheetLimitDistance - FinalStepMm);
                    if (finalDistance > maxAllowed)
                    {
                        log("  [WIZJA] Wyliczone " + finalDistance.ToString("0") + "mm wychodziłoby poza arkusz - ograniczam do " + maxAllowed.ToString("0") + "mm.");
                        finalDistance = maxAllowed;
                    }
                }

                log("  [WIZJA] Wybrana odległość: " + finalDistance.ToString("0") + "mm (" + ClearanceBeyondLastLineMm.ToString("0") + "mm za najdalszą wykrytą linią/opisem w tym kierunku).");

                SetFixedDistance(rd, activeDrawing, scale, sign * finalDistance);
                Thread.Sleep(200);

                beforeShot.Dispose();
                reference = WindowCapture.CaptureWindow(teklaHwnd);
                return true;
            }
            catch (Exception ex)
            {
                log("  [WIZJA] Błąd podczas wizualnego wymuszania strony - użyto trybu awaryjnego (Free). Błąd: " + ex.Message);
                return false;
            }
        }

        private static Tekla.Structures.Geometry3d.Point CircumCenter(
            Tekla.Structures.Geometry3d.Point p1, Tekla.Structures.Geometry3d.Point p2, Tekla.Structures.Geometry3d.Point p3)
        {
            double ax = p1.X, ay = p1.Y;
            double bx = p2.X, by = p2.Y;
            double cx = p3.X, cy = p3.Y;

            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-9)
            {
                throw new InvalidOperationException("Punkty łuku są współliniowe.");
            }

            double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

            return new Tekla.Structures.Geometry3d.Point(ux, uy, 0);
        }

        private (double cx, double cy, int count, bool valid) ProbeSign(
            RadiusDimension rd, Drawing activeDrawing, double scale, int sign, double distanceMm,
            IntPtr teklaHwnd, System.Drawing.Bitmap beforeShot)
        {
            SetFixedDistance(rd, activeDrawing, scale, sign * distanceMm);
            Thread.Sleep(250);
            using (var shot = WindowCapture.CaptureWindow(teklaHwnd))
            {
                var diff = WindowCapture.DiffCentroid(beforeShot, shot);
                bool valid = diff.count >= MinDiffPixelsForValidProbe;
                return (diff.cx, diff.cy, diff.count, valid);
            }
        }

        /// <summary>
        /// Ustawia wymiar R na tryb Fixed z podanym (podpisanym) Distance w
        /// mm NA PAPIERZE (przeliczane przez skalę widoku na jednostki
        /// modelu) - w trybie Fixed to WŁAŚNIE znak Distance kontroluje,
        /// po której stronie linii bazowej ląduje tekst (w przeciwieństwie
        /// do trybu Free, gdzie strona jest ustalona na sztywno per wymiar
        /// i niezależna od żadnego atrybutu - patrz komentarz w
        /// TryPlaceOutside).
        /// </summary>
        private static void SetFixedDistance(RadiusDimension rd, Drawing activeDrawing, double scale, double distanceMm)
        {
            var attrs = rd.Attributes;
            attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, Math.Abs(distanceMm) / scale));
            rd.Attributes = attrs;
            rd.Distance = distanceMm / scale;
            rd.Modify();
            activeDrawing.CommitChanges();
        }

        /// <summary>
        /// Rozstawia JEDEN wymiar R WBUDOWANYM w Teklę silnikiem
        /// auto-rozstawiania (Attributes.Placing = Placings.Free) - ten sam
        /// mechanizm co przy StraightDimensionSet. Używane jako wariant
        /// AWARYJNY (gdy wizyjne wymuszanie strony, TryPlaceOutside, jest
        /// niedostępne albo zawiedzie) - Free unika kolizji z innymi
        /// elementami, ale (potwierdzone empirycznie) czasem ląduje w
        /// środku konturu części, bo wybrana strona jest ustalona na sztywno
        /// per wymiar i żaden atrybut (Direction Positive/Negative) tego nie
        /// zmienia.
        ///
        /// WAŻNE - dwuetapowość jest konieczna: samo ustawienie Placing=Free
        /// z nowymi parametrami wyszukiwania NIE wymusza ponownego
        /// przeliczenia, jeśli wymiar był już wcześniej w trybie Free
        /// (Tekla zdaje się cache'ować wynik). Trzeba najpierw przełączyć na
        /// Fixed z dowolnym Distance, zapisać, i DOPIERO wtedy przełączyć
        /// na Free ze świeżymi parametrami - potwierdzone empirycznie na
        /// żywym rysunku.
        ///
        /// WAŻNE - jednostki: PlacingDistanceAttributes (SearchMargin,
        /// MinimalDistance, MaximalDistance) są w jednostkach MODELU, tak
        /// samo jak zwykłe Distance - trzeba dzielić przez skalę widoku,
        /// inaczej (potwierdzone empirycznie) wymiar wyleci daleko poza
        /// widok przy widoku w powiększonej skali.
        /// </summary>
        private bool PlaceUsingFreeMode(RadiusDimension rd, Drawing activeDrawing, double scale, Action<string> log)
        {
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
                log("  Wymiar R rozstawiony trybem awaryjnym (Tekla Placing=Free, margines " + SearchMarginMm + "mm, zakres " + MinimalDistanceMm + "-" + MaximalDistanceMm + "mm na papierze).");
            }

            return modifyResult;
        }

        /// <summary>
        /// Dociąga opisy (Mark, np. "1*Ø13") bliżej tego, co opisują -
        /// domyślnie mają MaximalDistance=0 (bez limitu), więc Tekla czasem
        /// wyrzuca je bardzo daleko szukając wolnego miejsca. Ten sam
        /// dwuetapowy wzorzec reset-Fixed-potem-Free co dla wymiarów R, bo
        /// MarkBase.Attributes.PlacingAttributes dzieli tę samą logikę
        /// wyszukiwania (PlacingDistanceAttributes) co RadiusDimension.
        /// </summary>
        private List<(Mark, Mark.MarkAttributes)> TightenMarks(
            List<Mark> marks, Drawing activeDrawing, Dictionary<ViewBase, double> scaleCache, Action<string> log)
        {
            var history = new List<(Mark, Mark.MarkAttributes)>();

            foreach (var mark in marks)
            {
                try
                {
                    Mark.MarkAttributes originalAttrs = mark.Attributes;
                    double scale = GetViewScale(mark, scaleCache, log);

                    // Krok 1: reset "Fixed" z małą, neutralną odległością -
                    // wymusza świeże przeliczenie przy przełączeniu na "auto"
                    // (IsFixed=false), zamiast używać ewentualnego cache.
                    var resetAttrs = mark.Attributes;
                    resetAttrs.PlacingAttributes = new PlacingAttributes(
                        true,
                        new PlacingDistanceAttributes(2.0, ResetDistanceMm / scale),
                        resetAttrs.PlacingAttributes.PlacingQuarter);
                    mark.Attributes = resetAttrs;
                    mark.Modify();
                    activeDrawing.CommitChanges();
                    Thread.Sleep(200);

                    // Krok 2: "auto" (IsFixed=false) z ciasnym zakresem
                    // wyszukiwania (mm na papierze -> jednostki modelu), żeby
                    // opis został blisko, ale wciąż bez kolizji.
                    var tightAttrs = mark.Attributes;
                    tightAttrs.PlacingAttributes = new PlacingAttributes(
                        false,
                        new PlacingDistanceAttributes(MarkSearchMarginMm / scale, MarkMinimalDistanceMm / scale, MarkMaximalDistanceMm / scale),
                        tightAttrs.PlacingAttributes.PlacingQuarter);
                    mark.Attributes = tightAttrs;

                    bool modifyResult = mark.Modify();
                    activeDrawing.CommitChanges();
                    Thread.Sleep(200);

                    if (modifyResult)
                    {
                        history.Add((mark, originalAttrs));
                        log("  Opis dociągnięty bliżej (zakres " + MarkMinimalDistanceMm + "-" + MarkMaximalDistanceMm + "mm na papierze).");
                    }
                    else
                    {
                        log("  Jeden opis nie został zmodyfikowany (Modify() zwróciło false).");
                    }
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden opis – błąd: " + ex.Message);
                }
            }

            return history;
        }

        /// <summary>
        /// Zwraca skalę widoku (np. 5.0 dla rysunku szczegółowego "5:1"), w
        /// którym leży dany obiekt (wymiar R, opis...) - liczone raz na
        /// widok i zapamiętane w cache. Bezpieczny fallback = 1.0 (stare,
        /// "1mm = 1mm" zachowanie), jeśli nie da się odczytać widoku/skali.
        /// </summary>
        private static double GetViewScale(DrawingObject rd, Dictionary<ViewBase, double> cache, Action<string> log)
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

            // Cofnij w tym samym kroku dociągnięte opisy (Mark) - zdejmowane
            // ze stosu w parze z powyższym (jedno "Przesuń" = jeden wpis na
            // obu stosach, nawet jeśli na arkuszu nie było żadnych opisów).
            if (_markUndoStack.Count > 0)
            {
                var lastMarkMove = _markUndoStack.Pop();
                foreach (var entry in lastMarkMove)
                {
                    try
                    {
                        entry.mark.Attributes = entry.previousAttributes;
                        entry.mark.Modify();
                    }
                    catch (Exception ex)
                    {
                        log("  Pominięto jeden opis przy cofaniu – błąd: " + ex.Message);
                    }
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
