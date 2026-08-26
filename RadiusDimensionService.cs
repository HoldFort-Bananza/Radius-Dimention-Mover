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
        // --- Parametry wyszukiwania wolnego miejsca dla trybu AWARYJNEGO
        // (Placing=Free) - w jednostkach MODELU, tak jak Distance. ---
        private const double SearchMarginMm = 30.0;
        private const double MinimalDistanceMm = 15.0;
        private const double MaximalDistanceMm = 300.0;

        // Mały "neutralny" krok używany tylko po to, żeby wymusić świeże
        // przeliczenie przez Teklę przy przełączaniu Fixed -> Free (patrz
        // komentarz w PlaceUsingFreeMode).
        private const double ResetDistanceMm = 4.0;

        // --- Parametry podejścia "wizualnego" (WindowCapture) - patrz
        // dokumentacja TryPlaceSmart().
        //
        // WAŻNE - jednostki: RadiusDimension.Distance oraz ArcPoint1/2/3 są w
        // jednostkach MODELU, natomiast ViewBase.GetAxisAlignedBoundingBox()
        // zwraca wymiary NA PAPIERZE (potwierdzone empirycznie: blacha 538mm
        // w modelu, bbox szerokości 148mm przy skali 1:5). Dlatego wszystkie
        // odległości szukania wyrażamy jako UŁAMEK rozmiaru części w modelu
        // (bbox * skala), a nie jako stałe milimetry - inaczej te same
        // wartości oznaczają zupełnie inną odległość na rysunku detalu 5:1
        // niż na blachy 1:5 (na tym się właśnie wcześniej wywróciło:
        // "60mm" wychodziło realnie 12mm w modelu, czyli wciąż na krawędzi
        // blachy 538x141). ---

        // Ułamek rozmiaru części użyty jako odległość próbna do ustalenia,
        // w którą stronę (+/-) faktycznie wypada tekst wymiaru.
        private const double ProbeFraction = 0.25;
        private const double AnchorProbeFraction = 0.02;

        // --- Kiedy tekst wymiaru może zostać WEWNĄTRZ części ---
        // Zasada ustalona z użytkownikiem: w środku wolno zostawić tekst tylko
        // gdy część jest większa niż ten próg I NIE MA w sobie żadnego otworu
        // (wtedy w środku jest pusto). Mniejsza część albo obecność otworu =
        // tekst na zewnątrz, przy liniach wymiarowych.
        private const double MinPartSizeForInsideMm = 300.0;

        // Jak głęboko w część wchodzi tekst, gdy wolno mu tam zostać -
        // ułamek rozmiaru odniesienia.
        private const double InsideFraction = 0.10;

        // Maksymalny dopuszczalny ułamek nowo narysowanego wymiaru, który
        // wolno nałożyć na już istniejącą treść (patrz
        // WindowCapture.GetOverlapWithExisting). Próg jest luźny, bo leader
        // wychodzący z części z natury przecina stos linii wymiarowych i to
        // jest w porządku - chodzi o to, żeby sam TEKST nie wpadł w inny
        // tekst ani w otwór.
        private const double OutsideMaxOverlap = 0.015;

        // --- Wyszukiwanie miejsca NA ZEWNĄTRZ: szukamy NAJBLIŻSZEJ pozycji,
        // w której wymiar nie nakłada się na istniejącą treść. Krok jest
        // drobny, żeby nie przeskoczyć dobrego miejsca i nie wyrzucić wymiaru
        // dalej, niż potrzeba. ---
        private const double OutsideMinFraction = 0.06;
        private const double OutsideMaxFraction = 0.55;
        private const int OutsideSteps = 16;

        // Rozmiar (px) kwadratu sprawdzanego przy wykrywaniu krawędzi arkusza.
        private const int OccupancyBoxSizePx = 36;

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
            TightenMarks(marks, activeDrawing, scaleCache, log);

            // --- Przygotowanie "wizyjnego" wymuszania strony (poza kontur
            // części, nigdy do środka) - patrz TryPlaceSmart(). Jeśli
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
                        double scale = GetViewScale(rd, scaleCache, log);

                        bool placed = false;
                        if (visionAvailable)
                        {
                            placed = TryPlaceSmart(rd, activeDrawing, scale, teklaHwnd, ref reference, log);
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


            return result;
        }

        /// <summary>
        /// Umieszcza wymiar R w rozsądnym miejscu: NAJPIERW próbuje WEWNĄTRZ
        /// części (jeśli jest tam wolne miejsce - wtedy rysunek jest
        /// najbardziej zwarty), a dopiero gdy w środku jest ciasno, wypycha
        /// go NA ZEWNĄTRZ, za skrajną linię wymiarową.
        ///
        /// Potrzebne, bo tryb Placing=Free (patrz PlaceUsingFreeMode) wybiera
        /// kąt/stronę wg czegoś ustalonego "na sztywno" per wymiar, czego NIE
        /// da się zmienić żadnym atrybutem API (sprawdzone empirycznie:
        /// PlacingDirectionAttributes.Positive/Negative nie ma żadnego
        /// wpływu na wybraną stronę - trzy niezależne testy dały identyczny
        /// wynik).
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
        /// używana tylko do: (1) JEDNORAZOWEGO sprawdzenia na wymiar, czy
        /// Tekla przyjęła znak "+" Distance jako ruch w policzonym kierunku
        /// "na zewnątrz", czy przeciwnie (Tekla może stosować dowolną
        /// wewnętrzną konwencję, której nie da się odgadnąć bez jednego
        /// rzeczywistego pomiaru), (2) sprawdzenia, czy w środku części jest
        /// wolne miejsce, (3) wyszukania miejsca za skrajną linią wymiarową,
        /// jeśli w środku jest ciasno.
        ///
        /// Zwraca false (bez wyjątku), jeśli z jakiegokolwiek powodu nie da
        /// się tego wiarygodnie ustalić (np. zdegenerowana geometria łuku,
        /// brak zauważalnej zmiany na zrzutach) - wywołujący ma wtedy spaść
        /// do PlaceUsingFreeMode jako bezpiecznego wariantu awaryjnego.
        /// </summary>
        private bool TryPlaceSmart(
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

                // Fakty o części z MODELU: rozmiar i liczba otworów. Rozmiar
                // części jest też rozmiarem odniesienia dla wszystkich
                // odległości szukania.
                //
                // WAŻNE: kiedyś rozmiar odniesienia brał się z bounding boxa
                // WIDOKU - to był błąd z pętlą sprzężenia: bbox widoku rośnie,
                // gdy wymiary zostaną wyrzucone daleko, więc każde kolejne
                // uruchomienie liczyło coraz większe odległości (na blachy
                // 175mm doszło do 2600!). Rozmiar bryły z modelu jest stały i
                // niezależny od tego, gdzie aktualnie leżą wymiary.
                var facts = GetPartFacts(rd, log);

                double referenceSize = facts.valid
                    ? facts.maxSizeMm
                    : GetModelReferenceSize(rd, scale, log);
                if (referenceSize <= 0)
                {
                    log("  [WIZJA] Nie udało się ustalić rozmiaru części - pomijam wizualne umieszczanie.");
                    return false;
                }

                // Jedna próbna zmiana w stronę "+", żeby sprawdzić, czy
                // zgadza się z policzonym kierunkiem "na zewnątrz".
                var anchorProbe = ProbeSign(rd, activeDrawing, +1, referenceSize * AnchorProbeFraction, teklaHwnd, beforeShot);
                var posProbe = ProbeSign(rd, activeDrawing, +1, referenceSize * ProbeFraction, teklaHwnd, beforeShot);

                if (!anchorProbe.valid || !posProbe.valid)
                {
                    log("  [WIZJA] Brak wykrywalnej zmiany na zrzutach ekranu - pomijam wizualne umieszczanie.");
                    return false;
                }

                double observedDx = posProbe.cx - anchorProbe.cx;
                double observedDy = posProbe.cy - anchorProbe.cy;
                double dot = observedDx * expectedPixelDx + observedDy * expectedPixelDy;
                int sign = dot >= 0 ? +1 : -1;

                log("  [WIZJA] Rozmiar odniesienia " + referenceSize.ToString("0") + "mm, strona na zewnątrz: " + (sign > 0 ? "+" : "-") + ".");

                // --- Decyzja WEWNĄTRZ czy NA ZEWNĄTRZ ---
                // Zasada (ustalona z użytkownikiem): tekst może zostać w
                // środku części TYLKO gdy część jest większa niż
                // MinPartSizeForInsideMm i NIE MA w sobie żadnego otworu -
                // wtedy w środku jest pusto i nic nie zasłania. Jak jest
                // otwór albo część jest mała, tekst idzie na zewnątrz.
                bool insideAllowed = facts.valid
                    && facts.maxSizeMm > MinPartSizeForInsideMm
                    && facts.holeCount == 0;

                if (facts.valid)
                {
                    log("  [WIZJA] Część: max wymiar " + facts.maxSizeMm.ToString("0") + "mm, otworów: " + facts.holeCount
                        + " -> tekst " + (insideAllowed ? "MOŻE zostać w środku." : "musi iść na zewnątrz."));
                }
                else
                {
                    log("  [WIZJA] Nie udało się odczytać danych części z modelu - tekst idzie na zewnątrz (bezpieczniej).");
                }

                if (insideAllowed)
                {
                    // Do środka: znak przeciwny do "na zewnątrz". Dla
                    // RadiusDimension ujemny Distance przenosi tekst na
                    // przeciwną stronę środka okręgu, więc przy dużej pustej
                    // części ląduje w jej wnętrzu - i o to tu chodzi.
                    double insideDistance = referenceSize * InsideFraction;
                    SetFixedDistance(rd, activeDrawing, -sign * insideDistance);
                    Thread.Sleep(200);

                    log("  [WIZJA] Tekst zostawiony w środku części (odległość " + insideDistance.ToString("0") + ").");

                    beforeShot.Dispose();
                    reference = WindowCapture.CaptureWindow(teklaHwnd);
                    return true;
                }

                // Szukamy NAJBLIŻSZEJ pozycji na zewnątrz, w której wymiar nie
                // nakłada się na istniejącą treść - czyli tuż przy liniach
                // wymiarowych opisujących element, odsunięty o tyle, żeby nic
                // nie zasłaniać. Wcześniejsza wersja lądowała "za najdalszą
                // wykrytą linią" i przy rysunkach z rozbudowanym opisem
                // wyrzucała wymiar absurdalnie daleko (odstęp liczony jako
                // ułamek bounding boxa widoku, który obejmuje wszystkie linie
                // wymiarowe, a nie samą część).
                double outsideMin = referenceSize * OutsideMinFraction;
                double outsideStep = (referenceSize * (OutsideMaxFraction - OutsideMinFraction)) / OutsideSteps;

                double bestDistance = outsideMin;
                bool foundClear = false;

                for (int i = 0; i <= OutsideSteps; i++)
                {
                    double d = outsideMin + i * outsideStep;
                    SetFixedDistance(rd, activeDrawing, sign * d);
                    Thread.Sleep(200);
                    using (var shot = WindowCapture.CaptureWindow(teklaHwnd))
                    {
                        var check = WindowCapture.GetOverlapWithExisting(beforeShot, shot);
                        if (check.changed < MinDiffPixelsForValidProbe)
                        {
                            log("  [WIZJA] Skan " + d.ToString("0") + ": brak widocznej zmiany - pomijam.");
                            continue;
                        }

                        // Krawędź arkusza to TWARDY limit - dalej nie szukamy,
                        // lepiej zostawić wymiar bliżej niż wyrzucić go za
                        // ramkę rysunku.
                        var diff = WindowCapture.DiffCentroid(beforeShot, shot);
                        if (WindowCapture.HasFrameOrGuideColor(beforeShot, diff.cx, diff.cy, OccupancyBoxSizePx))
                        {
                            log("  [WIZJA] Skan " + d.ToString("0") + ": krawędź arkusza - dalej nie szukam.");
                            break;
                        }

                        log("  [WIZJA] Skan " + d.ToString("0")
                            + ": nałożenie=" + check.overlap.ToString("0.00")
                            + " (" + check.changed + "px)");

                        if (check.overlap <= OutsideMaxOverlap)
                        {
                            bestDistance = d;
                            foundClear = true;
                            break;
                        }

                        // Nic wolnego jeszcze nie znaleziono - zapamiętaj
                        // najdalszą sprawdzoną pozycję jako wariant ostatniej
                        // szansy (lepsze to niż zostawienie wymiaru na
                        // konturze części).
                        bestDistance = d;
                    }
                }

                log(foundClear
                    ? "  [WIZJA] Wybrana odległość: " + bestDistance.ToString("0") + " (najbliższe wolne miejsce na zewnątrz)."
                    : "  [WIZJA] Nie znaleziono w pełni wolnego miejsca - używam najdalszej sprawdzonej: " + bestDistance.ToString("0") + ".");

                SetFixedDistance(rd, activeDrawing, sign * bestDistance);
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

        /// <summary>
        /// Fakty o części, do której należy wymiar, wzięte WPROST Z MODELU
        /// (nie z analizy pikseli): największy wymiar bryły w mm oraz liczba
        /// otworów. To one decydują, czy tekst wymiaru może zostać w środku
        /// części (patrz TryPlaceSmart).
        ///
        /// Droga: obiekt rysunkowy Part w tym samym widoku -> jego
        /// ModelIdentifier -> Model.SelectModelObject -> bryła i śruby/otwory.
        /// Rysunkowy Part sam nie ma żadnych danych geometrycznych, dlatego
        /// trzeba zejść do modelu.
        ///
        /// Za otwory liczymy zarówno śruby (GetBolts - stąd biorą się opisy
        /// typu "1*Ø13"), jak i wycięcia (GetBooleans), bo otwór może być
        /// zrobiony jednym albo drugim.
        /// </summary>
        private static (double maxSizeMm, int holeCount, bool valid) GetPartFacts(RadiusDimension rd, Action<string> log)
        {
            try
            {
                ViewBase view = rd.GetView();
                if (view == null)
                {
                    return (0, 0, false);
                }

                var model = new Tekla.Structures.Model.Model();
                if (!model.GetConnectionStatus())
                {
                    return (0, 0, false);
                }

                double maxSize = 0;
                int holes = 0;
                bool found = false;

                DrawingObjectEnumerator parts = view.GetAllObjects(typeof(Part));
                while (parts.MoveNext())
                {
                    if (!(parts.Current is Part drawingPart))
                    {
                        continue;
                    }

                    var modelObject = model.SelectModelObject(drawingPart.ModelIdentifier);
                    if (!(modelObject is Tekla.Structures.Model.Part modelPart))
                    {
                        continue;
                    }

                    found = true;

                    var solid = modelPart.GetSolid();
                    if (solid != null)
                    {
                        double dx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                        double dy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                        double dz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
                        maxSize = Math.Max(maxSize, Math.Max(dx, Math.Max(dy, dz)));
                    }

                    var bolts = modelPart.GetBolts();
                    while (bolts.MoveNext())
                    {
                        holes++;
                    }

                    var booleans = modelPart.GetBooleans();
                    while (booleans.MoveNext())
                    {
                        holes++;
                    }
                }

                return (maxSize, holes, found && maxSize > 0);
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać danych części z modelu: " + ex.Message);
                return (0, 0, false);
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

        /// <summary>
        /// Rozmiar odniesienia CZĘŚCI w jednostkach modelu - krótszy bok
        /// bounding boxa widoku przeskalowany do modelu.
        ///
        /// GetAxisAlignedBoundingBox() zwraca wymiary NA PAPIERZE, a Distance
        /// i ArcPoint1/2/3 są w jednostkach MODELU (potwierdzone empirycznie:
        /// blacha 538mm w modelu miała bbox szerokości 148mm przy skali 1:5),
        /// więc trzeba przemnożyć przez skalę. Bierzemy KRÓTSZY bok, bo to on
        /// ogranicza, jak głęboko w część można wejść, nie robiąc przelotu na
        /// drugą stronę.
        /// </summary>
        private static double GetModelReferenceSize(RadiusDimension rd, double scale, Action<string> log)
        {
            try
            {
                ViewBase view = rd.GetView();
                if (view == null)
                {
                    return 0;
                }

                var box = view.GetAxisAlignedBoundingBox();
                double shorterPaperSide = Math.Min(box.Width, box.Height);
                if (shorterPaperSide <= 1e-6)
                {
                    return 0;
                }

                return shorterPaperSide * scale;
            }
            catch (Exception ex)
            {
                log?.Invoke("  [DIAG] Nie udało się odczytać rozmiaru widoku: " + ex.Message);
                return 0;
            }
        }

        private (double cx, double cy, int count, bool valid) ProbeSign(
            RadiusDimension rd, Drawing activeDrawing, int sign, double modelDistance,
            IntPtr teklaHwnd, System.Drawing.Bitmap beforeShot)
        {
            SetFixedDistance(rd, activeDrawing, sign * modelDistance);
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
        /// jednostkach MODELU - w trybie Fixed to WŁAŚNIE znak Distance
        /// kontroluje, po której stronie linii bazowej ląduje tekst (w
        /// przeciwieństwie do trybu Free, gdzie strona jest ustalona na
        /// sztywno per wymiar i niezależna od żadnego atrybutu - patrz
        /// komentarz w TryPlaceSmart).
        /// </summary>
        private static void SetFixedDistance(RadiusDimension rd, Drawing activeDrawing, double modelDistance)
        {
            var attrs = rd.Attributes;
            attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
                DimensionSetBaseAttributes.Placings.Fixed,
                new PlacingDirectionAttributes(true, true),
                new PlacingDistanceAttributes(2.0, Math.Abs(modelDistance)));
            rd.Attributes = attrs;
            rd.Distance = modelDistance;
            rd.Modify();
            activeDrawing.CommitChanges();
        }

        /// <summary>
        /// Rozstawia JEDEN wymiar R WBUDOWANYM w Teklę silnikiem
        /// auto-rozstawiania (Attributes.Placing = Placings.Free) - ten sam
        /// mechanizm co przy StraightDimensionSet. Używane jako wariant
        /// AWARYJNY (gdy wizyjne wymuszanie strony, TryPlaceSmart, jest
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
        private void TightenMarks(
            List<Mark> marks, Drawing activeDrawing, Dictionary<ViewBase, double> scaleCache, Action<string> log)
        {
            foreach (var mark in marks)
            {
                try
                {
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
        /// Krótki opis tego, co jest teraz otwarte w Tekli - wyłącznie do
        /// pokazania w podpisie pod przyciskiem. Przycisk "Przesuń" jest
        /// zawsze klikalny, więc nic tu nie decyduje o jego stanie.
        /// </summary>
        public string GetCurrentDrawingDescription()
        {
            try
            {
                var drawingHandler = new DrawingHandler();
                if (!drawingHandler.GetConnectionStatus())
                {
                    return "Brak połączenia z Teklą - uruchom Teklę i otwórz rysunek.";
                }

                Drawing activeDrawing = drawingHandler.GetActiveDrawing();
                if (activeDrawing == null)
                {
                    return "Brak otwartego rysunku - otwórz rysunek w edytorze rysunków Tekli.";
                }

                return "Rysunek: " + activeDrawing.Name;
            }
            catch (Exception ex)
            {
                return "Nie udało się odczytać stanu Tekli: " + ex.Message;
            }
        }
    }
}
