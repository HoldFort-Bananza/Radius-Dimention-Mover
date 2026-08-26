[← Spis treści](index.md)

# 7. Diagnostyka

## Log sesji

Każde uruchomienie zapisuje pełny log do
`logs\session_<data_godzina>.log` obok pliku `.exe`. Okno pokazuje to samo.

Przykładowy log poprawnego przebiegu:

```
Aktywny rysunek: Einzelteil Blech
Znaleziono 2 wymiar(ów) R oraz 5 opis(ów) (Mark).
  Dociągnięto 5 opis(ów) bliżej elementu (zakres 10-60mm na papierze).
  Część: płaszczyzna 168 x 175mm, śruby: 3, wycięcia: 0, promień łuku 20mm.
  -> tekst NA ZEWNĄTRZ (Distance=-17).
  Część: płaszczyzna 168 x 175mm, śruby: 3, wycięcia: 0, promień łuku 20mm.
  -> tekst NA ZEWNĄTRZ (Distance=-17).
  Opis odsunięty o 19mm w bok, żeby nie zasłaniał wymiaru R.
  Opis odsunięty o 9mm w bok, żeby nie zasłaniał wymiaru R.
```

### Jak czytać log

| Linia | Co mówi |
|---|---|
| `Część: płaszczyzna A x B, śruby: N, wycięcia: M` | Dane, na których oparta jest decyzja. `A` to **krótszy** wymiar — porównaj z `InsideMinShortFaceMm`. |
| `-> tekst NA ZEWNĄTRZ` / `WEWNĄTRZ` | Wynik decyzji + użyte `Distance`. |
| `Opis odsunięty o Xmm` | Wykryto kolizję z linią odniesienia i skorygowano. |
| `Żaden opis nie kolidował` | Nic nie wymagało korekty. **Jeśli wizualnie nachodzą, sprawdź `LeaderCheckLengthFactor`.** |
| `Wymiar R rozstawiony trybem awaryjnym` | Geometria zawiodła, użyto `Placing=Free`. Zbadaj dlaczego. |
| `[DIAG] Nie udało się odczytać danych części z modelu` | Nie ma połączenia z modelem albo `SelectModelObject` nic nie zwrócił. |

## Podglądanie danych rysunku

Nie ma tego w wydawanym programie — to jednorazowe rusztowanie, które warto
odtworzyć, gdy trzeba zrozumieć, dlaczego decyzja wyszła tak, a nie inaczej.
Wzorzec: klasa `Inspector` + przełącznik w `Program.cs`.

```csharp
// Program.cs — tymczasowo
if (args.Length > 0 && args[0] == "--inspect") { Inspector.Inspect(); return; }
```

```csharp
var dh = new DrawingHandler();
Drawing dwg = dh.GetActiveDrawing();
var model = new Tekla.Structures.Model.Model();

var en = dwg.GetSheet().GetAllObjects();
while (en.MoveNext())
{
    if (!(en.Current is Part dp)) continue;
    var mo = model.SelectModelObject(dp.ModelIdentifier);
    if (!(mo is Tekla.Structures.Model.Part mp)) continue;

    var s = mp.GetSolid();
    Console.WriteLine($"{mp.Name} {mp.Profile.ProfileString}  " +
        $"{s.MaximumPoint.X - s.MinimumPoint.X:0.#} x " +
        $"{s.MaximumPoint.Y - s.MinimumPoint.Y:0.#} x " +
        $"{s.MaximumPoint.Z - s.MinimumPoint.Z:0.#}");

    int bolts = 0; var be = mp.GetBolts(); while (be.MoveNext()) bolts++;
    int bools = 0; var bo = mp.GetBooleans(); while (bo.MoveNext()) bools++;
    Console.WriteLine($"   śruby={bolts} wycięcia={bools}");
}
```

Realny wynik takiego podglądu:

```
=== Einzelteil Blech (mark [31758])
-- drawing Part
   model obj = Tekla.Structures.Model.ContourPlate
   name=Rippe profil=BL10
   bryła 10 x 65.5 x 180.8  -> max=180.8
   -- GetBolts --      razem=0
   -- GetBooleans --   razem=0
```

Właśnie ten odczyt pokazał, że kryterium było złe: porównywano 300 mm z **max**
wymiarem (180,8), a decydować powinien krótszy wymiar płaszczyzny.

### Co warto wypisywać

| Obiekt | Przydatne |
|---|---|
| `Part` (model) | `GetSolid()` min/max, `GetBolts()`, `GetBooleans()`, `Profile.ProfileString` |
| `RadiusDimension` | `ArcPoint1/2/3`, `Distance`, policzony circumcenter i promień |
| `StraightDimensionSet` | `Distance` + `GetObjects()` → `StraightDimension.StartPoint/EndPoint` |
| `Mark` | `InsertionPoint`, `GetAxisAlignedBoundingBox()` |
| `View` | `Attributes.Scale`, `GetAxisAlignedBoundingBox()` (⚠️ w mm na papierze) |

### Listowanie i przełączanie rysunków

Przydaje się, żeby przetestować konkretny przypadek bez klikania w Tekli:

```csharp
var drawings = dh.GetDrawings();          // DrawingEnumerator
while (drawings.MoveNext()) { /* .Name, .Mark, .Title1..3 */ }

dh.SetActiveDrawing(drawing, false, false);
```

Uwaga: zmienia aktywny rysunek u użytkownika — nie rób tego bez uzgodnienia.

## Uruchamianie bez UI

Do szybkiego testu bez klikania (też tymczasowy przełącznik):

```csharp
if (args.Length > 0 && args[0] == "--testrun")
{
    var service = new RadiusDimensionService();
    var result = service.AutoPlaceWithCollisionAvoidance(Console.WriteLine);
    Console.WriteLine($"MovedCount={result.MovedCount}, TotalCount={result.TotalCount}");
    return;
}
```

```bash
cd bin/x64/Debug/net48
./RadiusDimensionMover.exe --testrun
```

**Pamiętaj usunąć rusztowanie przed commitem** — wydawany program ma tylko
`Main()` uruchamiające `MainForm`.

## Weryfikacja wzrokowa

Program nie czyta ekranu i nie powinien, ale **człowiek sprawdzający wynik
musi zobaczyć rysunek**. Jeśli robisz zrzut okna Tekli do weryfikacji, użyj
`PrintWindow` z flagą `PW_RENDERFULLCONTENT = 2` — jest odporny na zasłonięcia
innymi oknami. `Graphics.CopyFromScreen` łapie to, co faktycznie jest na
wierzchu, i potrafi pokazać cudze okno zamiast Tekli.

## Typowe objawy

| Objaw | Gdzie szukać |
|---|---|
| Wszystkie wymiary po złej stronie | `OutwardSign` |
| Wymiary za daleko / za blisko | `OutsideFraction` |
| Wymiar w środku, choć nie powinien | `InsideMinShortFaceMm`, `InsideRoomRadiusFactor`, liczba otworów w logu |
| Wymiar na zewnątrz, choć mógłby w środku | to samo + sprawdź, czy `GetBooleans()` nie liczy ścięcia narożnika jako otworu |
| Opisy nachodzą na wymiar | `LeaderCheckLengthFactor` (za krótki zasięg), potem `MarkClearanceMm` |
| Opisy odjechały od otworów | `MarkClearanceMm` za duży, `MarkMaximalDistanceMm` |
| `GetConnectionStatus()` = false przy działającej Tekli | brak `TSAppConfigPatcherTask` / zły `.exe.config` |
| Przycisk nie reaguje na zmianę rysunku | zdarzenia niezarejestrowane — patrz log przy starcie |
