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

        // --- Rozstawianie wymiarów R: WYŁĄCZNIE ze współrzędnych z API
        // (ArcPoint1/2/3, bryła części, StraightDimensionSet.Distance).
        // Wszystko w jednostkach MODELU, tak jak Distance. Żadnych zrzutów
        // ekranu ani analizy pikseli - patrz TryPlaceByGeometry(). ---

        // Znak Distance oznaczający kierunek NA ZEWNĄTRZ (od środka łuku).
        // Ustalone empirycznie: RadiusDimension nie udostępnia swojej
        // pozycji, więc konwencji nie da się odczytać z API, ale kilkanaście
        // niezależnych pomiarów na trzech rysunkach dało za każdym razem ten
        // sam wynik. Jeśli kiedyś wyjdzie odwrotnie - wystarczy tu -1/+1.
        private const double OutwardSign = -1.0;

        // --- Kiedy tekst wymiaru może zostać WEWNĄTRZ części ---
        // Zasada ustalona z użytkownikiem: w środku wolno zostawić tekst tylko
        // gdy część jest większa niż ten próg I NIE MA w sobie żadnego otworu
        // (wtedy w środku jest pusto). Mniejsza część albo obecność otworu =
        // tekst na zewnątrz, przy liniach wymiarowych.
        private const double MinPartSizeForInsideMm = 300.0;

        // Jak głęboko w część wchodzi tekst, gdy wolno mu tam zostać -
        // ułamek największego wymiaru części.
        private const double InsideFraction = 0.10;

        // Jak daleko na zewnątrz od łuku ląduje tekst - ułamek największego
        // wymiaru części. Wartość dobrana z rzeczywistych rysunków: na blachy
        // 175mm daje ~18mm, czyli tuż za opisem elementu.
        private const double OutsideFraction = 0.10;

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

            foreach (var rd in radiusDimensions)
            {
                try
                {
                    double scale = GetViewScale(rd, scaleCache, log);

                    if (!TryPlaceByGeometry(rd, activeDrawing, log))
                    {
                        // Geometria się nie udała (np. zdegenerowany łuk) -
                        // zostaje wbudowany silnik Tekli jako wariant
                        // awaryjny, żeby program zawsze coś zrobił.
                        if (!PlaceUsingFreeMode(rd, activeDrawing, scale, log))
                        {
                            log("  Jeden wymiar R nie został zmodyfikowany (Modify() zwróciło false).");
                            continue;
                        }
                    }

                    result.MovedCount++;
                }
                catch (Exception ex)
                {
                    log("  Pominięto jeden wymiar R – błąd: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Ustawia JEDEN wymiar R, licząc wszystko WYŁĄCZNIE ze współrzędnych
        /// z API Tekli - żadnych zrzutów ekranu ani analizy pikseli.
        ///
        /// Dane wejściowe (wszystko w jednostkach MODELU, ta sama przestrzeń
        /// co Distance):
        /// - `ArcPoint1/2/3` -> środek i promień łuku (circumcenter),
        /// - bryła części z modelu -> rozmiar i liczba otworów,
        /// - `StraightDimensionSet.Distance` -> jak daleko od elementu leżą
        ///   już istniejące łańcuchy wymiarowe.
        ///
        /// Zasada rozstawiania (ustalona z użytkownikiem):
        /// - część większa niż MinPartSizeForInsideMm i BEZ otworów -> tekst
        ///   zostaje wewnątrz części (jest tam pusto),
        /// - część z otworem albo mniejsza -> tekst na zewnątrz, odsunięty o
        ///   OutsideOffsetMm ZA najdalszy istniejący łańcuch wymiarowy, żeby
        ///   współgrał z opisem elementu.
        ///
        /// ZNAK Distance: ujemny = NA ZEWNĄTRZ (od środka łuku), dodatni = do
        /// wnętrza części. Ustalone empirycznie - RadiusDimension nie
        /// udostępnia swojej pozycji, więc konwencji nie da się odczytać z
        /// API; kilkanaście niezależnych pomiarów na trzech różnych rysunkach
        /// dało za każdym razem ten sam wynik. Jeśli kiedyś okaże się
        /// odwrotnie, wystarczy odwrócić OutwardSign.
        /// </summary>
        private bool TryPlaceByGeometry(RadiusDimension rd, Drawing activeDrawing, Action<string> log)
        {
            Tekla.Structures.Geometry3d.Point center;
            try
            {
                center = CircumCenter(rd.ArcPoint1, rd.ArcPoint2, rd.ArcPoint3);
            }
            catch (Exception ex)
            {
                log("  Nie udało się policzyć środka łuku: " + ex.Message);
                return false;
            }

            double radius = Distance2D(center, rd.ArcPoint2);
            if (radius < 1e-6)
            {
                log("  Zdegenerowana geometria łuku (promień ~0).");
                return false;
            }

            var facts = GetPartFacts(rd, log);
            if (!facts.valid)
            {
                log("  Nie udało się odczytać danych części z modelu.");
                return false;
            }

            bool insideAllowed = facts.maxSizeMm > MinPartSizeForInsideMm && facts.holeCount == 0;

            log("  Część: max wymiar " + facts.maxSizeMm.ToString("0") + "mm, otworów: " + facts.holeCount
                + ", promień łuku " + radius.ToString("0") + "mm.");

            double distance;
            if (insideAllowed)
            {
                // Do wnętrza dużej, pustej części - na tyle głęboko, żeby
                // tekst nie siedział na samej krawędzi.
                distance = -OutwardSign * facts.maxSizeMm * InsideFraction;
                log("  -> tekst WEWNĄTRZ części (Distance=" + distance.ToString("0") + ").");
            }
            else
            {
                // Na zewnątrz - odległość jako ułamek rozmiaru części.
                //
                // Dlaczego NIE liczymy tego z StraightDimensionSet.Distance,
                // choć taka wartość jest w API: nie jest w tej samej skali co
                // RadiusDimension.Distance. Na blachy 175mm łańcuchy raportują
                // Distance 25-120, a wstawienie 120 wyrzuciło tekst poza
                // arkusz, podczas gdy ~15-25 ląduje tuż za opisem. Ponieważ
                // RadiusDimension NIE udostępnia swojej pozycji przez API,
                // nie ma czym tego przeliczyć bez patrzenia na ekran - a tego
                // nie robimy. Rozmiar części z modelu jest stabilną i
                // wystarczającą podstawą.
                distance = OutwardSign * facts.maxSizeMm * OutsideFraction;
                log("  -> tekst NA ZEWNĄTRZ (Distance=" + distance.ToString("0") + ").");
            }

            SetFixedDistance(rd, activeDrawing, distance);
            return true;
        }

        private static double Distance2D(Tekla.Structures.Geometry3d.Point a, Tekla.Structures.Geometry3d.Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
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
