[← Spis treści](index.md)

# 3. Fakty o API Tekli

Wszystko na tej stronie zostało **sprawdzone empirycznie** na żywych rysunkach
w Tekla Structures 2025. To nie są domysły z dokumentacji.

---

## Jednostki

To najczęstsze źródło błędów w tym projekcie. Nie ma jednej konwencji:

| Właściwość | Jednostka | Uwagi |
|---|---|---|
| `RadiusDimension.Distance` | **model** | |
| `RadiusDimension.ArcPoint1/2/3` | **model** | ta sama przestrzeń co `Distance` |
| `StraightDimension.StartPoint/EndPoint` | **model** | punkty leżące **na części** |
| `Solid.MinimumPoint/MaximumPoint` | **model** | |
| `Mark.InsertionPoint`, `GetAxisAlignedBoundingBox()` | **model** | |
| `PlacingDistanceAttributes` (Search/Min/Max) | **model** | dla wymiarów i opisów |
| `ViewBase.GetAxisAlignedBoundingBox()` | **papier** | ⚠️ inaczej niż wszystko wyżej |
| `View.Attributes.Scale` | — | np. 5.0 dla 1:5 i dla 5:1 |

**Dowód na rozbieżność:** blacha o długości 538 mm w modelu miała bbox widoku
o szerokości 148 mm przy skali 1:5.

Praktyczna konsekwencja: jeśli w kodzie chcesz wyrazić „X mm na papierze",
podziel przez `scale` przed wysłaniem do API. Tak działa etap dociągania opisów
(`MarkMinimalDistanceMm / scale`). Rozstawianie wymiarów R **nie używa** mm na
papierze — liczy ułamkami rzeczywistego rozmiaru części, żeby działać tak samo
w każdej skali.

---

## Czego API **nie** daje

### `RadiusDimension` nie zna swojej pozycji

To ograniczenie kształtuje cały projekt.

- `ArcPoint1/2/3` opisują **łuk**, nie tekst. Sprawdzone: identyczne
  współrzędne przy `Distance = 3` i `Distance = 88,7`.
- Nie implementuje żadnego akcesora bounding boxa.
- `GetRelatedObjects()` zwraca 0 obiektów.
- `GetDimensionSet()` rzuca wyjątkiem dla samodzielnego wymiaru R.

**Skutek:** nie da się sprawdzić, gdzie tekst wylądował, ani skalibrować, co
właściwie znaczy `Distance`. Wszystkie liczby w kodzie dotyczące pozycji tekstu
są oszacowaniami wyznaczonymi na rzeczywistych rysunkach.

### `Distance` nie jest odległością tekstu

Przy `Distance = 23` na blachy 66 × 181 tekst odjechał od łuku **~100 mm**.
Przy `Distance = 17` na blachy 175 mm opis stykający się z tekstem leżał
**~140 mm** wzdłuż promienia. Zależność nie jest liniowa w oczywisty sposób i
nie udało się jej ustalić bez odczytu pozycji, którego API nie udostępnia.

### `Tekla.Structures.Drawing.Part` nie ma geometrii

Pełna lista publicznych członków to `Attributes`, `Modify`,
`GetRelatedObjects`, gettery właściwości użytkownika — **nic spatialnego**.
Po wymiary trzeba iść do modelu przez `ModelIdentifier`.

### `StraightDimensionSet.Distance` nie jest w skali `RadiusDimension.Distance`

Na blachy 175 mm łańcuchy wymiarowe raportują `Distance` 25–120. Wstawienie
120 jako `Distance` wymiaru R wyrzuciło tekst **poza arkusz**, podczas gdy
~15–25 ląduje tuż za opisem. Wygląda to na naturalne odniesienie („jak daleko
leżą linie wymiarowe"), ale nim nie jest.

Dodatkowo: `Distance` na **pojedynczym** `StraightDimension` wewnątrz łańcucha
znaczy coś jeszcze innego — bliżej długości mierzonego odcinka niż odsunięcia
linii (na blachy 175 mm wychodziło z niego 196 mm).

---

## Co API daje i jest przydatne

### Geometria łuku

`ArcPoint1/2/3` → circumcenter → środek i promień. Sprawdzone: `R20` daje
promień 20,000. To jedyne w pełni wiarygodne źródło kierunku „na zewnątrz".

### Dane części z modelu

```csharp
var model = new Tekla.Structures.Model.Model();
var mo = model.SelectModelObject(drawingPart.ModelIdentifier);
if (mo is Tekla.Structures.Model.Part mp)
{
    var s = mp.GetSolid();          // MinimumPoint / MaximumPoint
    var bolts = mp.GetBolts();      // ModelObjectEnumerator
    var bools = mp.GetBooleans();   // ModelObjectEnumerator
}
```

Blachy konturowe wracają jako `Tekla.Structures.Model.ContourPlate`
(dziedziczy po `Part`).

⚠️ **`GetBooleans()` zwraca wszystkie operacje boole'owskie**, nie tylko otwory
— ścięty narożnik też się policzy. Dlatego program loguje śruby i wycięcia
osobno; jeśli kiedyś zacznie fałszywie wykrywać „otwory", to jest pierwsze
miejsce do sprawdzenia.

### Opisy (`Mark`) mają pełną geometrię — i są przesuwalne

W przeciwieństwie do wymiarów R:

| Członek | Do czego |
|---|---|
| `InsertionPoint` | **get i set** — można przesuwać wprost |
| `GetAxisAlignedBoundingBox()` | realny obrys tekstu (np. 53,2 × 18,5 mm) |
| `GetObjectAlignedBoundingBox()` | obrys w osiach obiektu |
| `Placing`, `Attributes.PlacingAttributes` | tryb auto/fixed + zakresy |

Na tym opiera się cały etap odsuwania opisów.

Ustawienie `InsertionPoint` działa tylko przy `IsFixed = true` — inaczej Tekla
przeliczy pozycję automatem i przesunięcie zniknie.

### Wymiary proste mają punkty

`StraightDimension.StartPoint/EndPoint` to realne współrzędne modelu **na
części** (dla blachy 175 × 168 wychodziły punkty typu `(0,−64)`,
`(174.7,−84)`). Można z nich odtworzyć obrys części w przestrzeni widoku —
program tego dziś nie robi, ale to dostępna droga.

### Zdarzenia UI

`Tekla.Structures.Drawing.UI.Events` udostępnia m.in.:

```
DrawingLoaded              ← wejście na inny rysunek
DrawingEditorOpened
DrawingEditorClosed
SelectionChange
DrawingListSelectionChanged
```

oraz `Tekla.Structures.Model.Events` → `TeklaStructuresExit`, `ModelLoad`.

Szczegóły użycia: [Architektura](01-architektura.md#nasłuch-zdarzeń-tekli).

### Listowanie i otwieranie rysunków

```csharp
DrawingHandler.GetDrawings()                        // DrawingEnumerator
DrawingHandler.SetActiveDrawing(drawing, bool, bool)
DrawingHandler.IsAnyDrawingOpen()
DrawingHandler.CloseActiveDrawing(bool)
```

Program tego nie używa (pracuje na aktywnym rysunku), ale przydaje się w
diagnostyce.

---

## `Placing=Free` wymaga dwuetapowego przełączenia

`RadiusDimensionAttributes.Placing` jest **odziedziczone** z
`DimensionSetBaseAttributes` — nie widać go przy refleksji z `DeclaredOnly`,
dlatego długo było przeoczone.

Samo ustawienie `Free` z nowymi parametrami **nie wymusza przeliczenia**, jeśli
wymiar już był w trybie `Free` — Tekla zdaje się cache'ować wynik. Trzeba:

1. przełączyć na `Fixed` z dowolnym `Distance`, `Modify()`, `CommitChanges()`,
2. i **dopiero potem** na `Free` ze świeżymi parametrami, `Modify()`,
   `CommitChanges()`.

W kodzie robi to `PlaceUsingFreeMode` (z krótkimi `Thread.Sleep` — to jedyne
miejsce, gdzie zostały, i tylko w rzadkiej ścieżce awaryjnej).

---

## Jak badać API

Refleksja z PowerShella na zbudowanym wyjściu (`bin\x64\Debug\net48`, gdzie
wszystkie zależności są już obok):

```powershell
$d = [Reflection.Assembly]::LoadFrom('Tekla.Structures.Drawing.dll')
$t = $d.GetType('Tekla.Structures.Drawing.MarkBase')
$t.GetMembers([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance) |
  Sort-Object Name -Unique | ForEach-Object { Write-Host $_.MemberType $_.Name }
```

Dwie pułapki:

- **Nie używaj `DeclaredOnly`** — przeoczysz odziedziczone właściwości (tak
  właśnie przeoczono `Attributes.Placing`).
- `GetTypes()` rzuca `ReflectionTypeLoadException` — łap i użyj
  `$_.Exception.Types | Where-Object { $_ -ne $null }`.
