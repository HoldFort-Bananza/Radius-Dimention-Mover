[← Spis treści](index.md)

# 2. Jak to działa — algorytm

Wszystko liczone jest **analitycznie i jednym rachunkiem**. Program nie próbuje
kolejnych pozycji i nie sprawdza wyniku po fakcie (nie ma czym — patrz
[Ograniczenia](08-ograniczenia.md)). Efekt jest natychmiastowy.

---

## Etap 1 — dociągnięcie opisów

Opisy otworów (`Mark`, np. `1*Ø13`) mają domyślnie `MaximalDistance = 0`, co
w Tekli znaczy **bez limitu**. Skutek: szukając wolnego miejsca Tekla potrafi
wyrzucić opis bardzo daleko od otworu, który opisuje.

Poprawka to jedno ustawienie na opis:

```csharp
attrs.PlacingAttributes = new PlacingAttributes(
    false,                                  // IsFixed = false → tryb auto
    new PlacingDistanceAttributes(
        MarkSearchMarginMm  / scale,         // 15 mm na papierze
        MarkMinimalDistanceMm / scale,       // 10 mm
        MarkMaximalDistanceMm / scale),      // 60 mm  ← tu jest sedno
    attrs.PlacingAttributes.PlacingQuarter);
```

Tekla dalej sama wybiera miejsce (unika kolizji), ale w zakresie 10–60 mm na
papierze, więc opis zostaje przy swoim otworze.

> Dzielenie przez `scale`: te parametry są w jednostkach **modelu**, a wartości
> w kodzie są wyrażone w mm **na papierze**. Patrz [Jednostki](03-api-tekli.md#jednostki).

---

## Etap 2 — położenie wymiaru R

### 2a. Geometria łuku

`RadiusDimension` daje trzy punkty leżące na łuku: `ArcPoint1/2/3`
(jednostki modelu — ta sama przestrzeń co `Distance`). Z nich liczymy środek
okręgu wzorem na **circumcenter**:

```
d  = 2·( ax·(by−cy) + bx·(cy−ay) + cx·(ay−by) )
ux = ( (ax²+ay²)(by−cy) + (bx²+by²)(cy−ay) + (cx²+cy²)(ay−by) ) / d
uy = ( (ax²+ay²)(cx−bx) + (bx²+by²)(ax−cx) + (cx²+cy²)(bx−ax) ) / d
```

Promień to odległość środka od któregokolwiek z punktów. Sprawdzone na żywym
rysunku: dla wymiaru opisanego jako `R20` wychodzi dokładnie 20,000.

Kierunek **na zewnątrz** to wektor od środka przez łuk:

```
dir = normalize(ArcPoint2 − środek)
```

Dla wypukłego zaokrąglenia narożnika (typowy przypadek) środek okręgu leży
**po stronie materiału**, więc ten kierunek wychodzi z części. To czysta
geometria, niezależna od czegokolwiek na ekranie.

### 2b. Dane części z modelu

Rysunkowy `Part` nie ma **żadnych** danych geometrycznych. Trzeba zejść do
modelu:

```
Part (rysunek) → .ModelIdentifier → Model.SelectModelObject(id)
              → Tekla.Structures.Model.Part
                   ├─ GetSolid()     → MinimumPoint / MaximumPoint
                   ├─ GetBolts()     → śruby (stąd opisy „1*Ø13")
                   └─ GetBooleans()  → wycięcia
```

Z trzech wymiarów bryły **odrzucamy najmniejszy** — to grubość blachy i nic nie
mówi o tym, ile jest miejsca na rysunku. Zostają dwa wymiary widocznej
płaszczyzny: `FaceLongMm` i `FaceShortMm`.

Przykład z rzeczywistego rysunku: bryła `10 × 65,5 × 180,8` →
grubość 10 (odrzucona), płaszczyzna **65,5 × 180,8**.

### 2c. Decyzja: wewnątrz czy na zewnątrz

Tekst zostaje **wewnątrz** części tylko gdy spełnione są **oba** warunki:

```csharp
bool roomInside = facts.FaceShortMm >= radius * InsideRoomRadiusFactor  // ≥ 3× promień
               && facts.FaceShortMm >= InsideMinShortFaceMm;            // ≥ 60 mm
bool insideAllowed = facts.HoleCount == 0 && roomInside;
```

- **`HoleCount == 0`** — jakikolwiek otwór oznacza, że w środku jest coś, co
  wymiar mógłby zasłonić.
- **`FaceShortMm`** — decyduje **krótszy** wymiar płaszczyzny, bo to on
  ogranicza, czy tekst się zmieści. Nie największy wymiar bryły: blacha
  65,5 × 180,8 bez otworów była przez to wyrzucana na zewnątrz, choć miejsca
  było dość.
- **Próg bezwzględny `InsideMinShortFaceMm`** — istnieje, bo `Distance` nie
  przekłada się wprost na odległość tekstu. Obecnie **60 mm**; przy tej
  wartości na blachy 66 × 181 oba wymiary R przelatują na skos przez materiał
  i lądują **pod** blachą. Wariant bezpieczny to 120 mm. Patrz
  [Parametry](05-parametry.md#insideminshortfacemm--najbardziej-wrażliwa-stała)
  i [Ograniczenia](08-ograniczenia.md#umieszczanie-wewnątrz-części).

### 2d. Ustawienie odległości

```csharp
// wewnątrz
distance = -OutwardSign * facts.FaceShortMm * InsideFraction;   // 0,35 × krótszy wymiar

// na zewnątrz
distance =  OutwardSign * facts.FaceLongMm  * OutsideFraction;  // 0,10 × dłuższy wymiar
```

Odległości są **ułamkiem rzeczywistego rozmiaru części**, nie stałymi
milimetrami — inaczej ta sama liczba znaczyłaby zupełnie coś innego na detalu
5:1 niż na blachy 1:5.

`OutwardSign = -1` to znak `Distance` odpowiadający kierunkowi na zewnątrz.
Ustalony empirycznie (API nie pozwala go wyliczyć) — kilkanaście niezależnych
pomiarów na trzech rysunkach dało za każdym razem ten sam wynik.

Zapis to jedno `Modify()`:

```csharp
attrs.Placing = new DimensionSetBaseAttributes.DimensionPlacingAttributes(
    DimensionSetBaseAttributes.Placings.Fixed,
    new PlacingDirectionAttributes(true, true),
    new PlacingDistanceAttributes(2.0, Math.Abs(distance)));
rd.Attributes = attrs;
rd.Distance = distance;
rd.Modify();
```

Tryb **`Fixed`**, nie `Free` — tylko wtedy nasza wyliczona wartość jest
respektowana. `Free` sam wybiera stronę i jej nie da się narzucić
(patrz [Ślepe uliczki](04-slepe-uliczki.md)).

---

## Etap 3 — odsunięcie kolidujących opisów

Wymiar R rysuje linię odniesienia od łuku do tekstu. Opis otworu, który na niej
leży, zasłania wymiar. Poprawiamy to **analitycznie**, bez szukania.

Dla każdego wymiaru R znamy półprostą (`LeaderRay`): punkt startowy na łuku,
kierunek i długość do sprawdzania. Dla każdego opisu:

```
v       = środek_opisu − początek_promienia
along   = v · dir                    (odległość wzdłuż linii)
lateral = |v − dir·along|             (odchyłka w bok)

needed  = ½·√(szerokość² + wysokość²) + MarkClearanceMm
deficit = needed − lateral
```

- `along < 0` → opis jest za łukiem, nie koliduje → pomiń
- `along > długość` → opis jest dalej niż sprawdzany odcinek → pomiń
- `deficit ≤ 0` → jest dość miejsca → pomiń
- inaczej → **przesuń prostopadle do linii dokładnie o `deficit`**

```csharp
mark.InsertionPoint = new Point(p.X + pushX, p.Y + pushY, p.Z);
```

Dwa niuanse:

1. **Prześwit liczony z przekątnej** opisu, nie z wysokości — linia odniesienia
   może biec pod dowolnym kątem, a wtedy „w poprzek" opisu jest właśnie
   przekątna.
2. **Przesunięty opis dostaje `IsFixed = true`.** Bez tego Tekla przy
   najbliższej okazji przeliczyłaby jego pozycję i przesunięcie by zniknęło.

Ponieważ przesuwamy **tylko w bok i tylko o brakującą różnicę**, opis zostaje
przy swoim otworze. Na rzeczywistym rysunku wyszło 9 mm i 19 mm — dokładnie
takie delikatne korekty.

> **Dlaczego sprawdzany odcinek jest długi** (`LeaderCheckLengthFactor = 1,5`
> × rozmiar części): API nie podaje pozycji tekstu, a tekst ląduje znacznie
> dalej, niż sugeruje `Distance`. Na blachy 175 mm przy `Distance = 17` opis
> stykający się z tekstem wymiaru leżał **~140 mm** wzdłuż promienia. Przy
> ciasnym oszacowaniu program w ogóle nie widział kolizji. Przeszacowanie jest
> tanie — opis leżący dalej zostanie i tak odsunięty tylko o swój `deficit`.

---

## Wariant awaryjny

Jeśli `TryPlaceByGeometry` zwróci `null` (zdegenerowany łuk, brak danych z
modelu), wymiar trafia do `PlaceUsingFreeMode` — wbudowanego silnika Tekli
`Placing = Free`. Unika kolizji, ale sam wybiera stronę, więc może wylądować
w środku obrysu. To wariant „lepsze to niż nic", nie ścieżka główna.

Uwaga: `Free` wymaga **dwuetapowego** przełączenia (`Fixed` → commit → `Free`),
inaczej Tekla nie przelicza pozycji. Szczegóły w
[Fakty o API](03-api-tekli.md#placingfree-wymaga-dwuetapowego-przełączenia).
