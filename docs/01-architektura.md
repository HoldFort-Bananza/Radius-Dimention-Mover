[← Spis treści](index.md)

# 1. Architektura

## Pliki źródłowe

| Plik | Odpowiedzialność |
|---|---|
| `Program.cs` | Punkt wejścia. Nic więcej — uruchamia `MainForm`. |
| `MainForm.cs` | Całe UI (jeden przycisk + status + log), połączenie z Teklą, nasłuch zdarzeń Tekli, zapis logu do pliku. |
| `RadiusDimensionService.cs` | **Cała logika.** Czytanie rysunku, liczenie pozycji, zapis do Tekli. Nie zna UI. |
| `RadiusDimensionMover.csproj` | .NET Framework 4.8, x64, WinForms. Pakiety NuGet `Tekla.Structures*` 2025.0.0. |
| `installer/setup.iss` | Inno Setup — instalator. |
| `installer/fetch-dependencies.ps1` | Pobiera biblioteki Tekla Open API z nuget.org **przy instalacji**. |
| `installer/TeklaEULA.txt` | Licencja Trimble/Tekla pokazywana w instalatorze. |

Podział jest prosty i warto go trzymać: **`MainForm` nie liczy geometrii, a
`RadiusDimensionService` nie dotyka UI.** Serwis komunikuje się z UI wyłącznie
przez `Action<string> log` i zwracany `MoveResult`.

## Dlaczego osobny .exe, a nie makro Tekli

Program używa dokładnie tego samego API co makra uruchamiane z wnętrza Tekli
(`DrawingHandler`), ale jest osobnym procesem. Zaleta: normalny cykl
budowania i debugowania w Visual Studio, własne UI, własny log.

Wymaga to pakietu `TSAppConfigPatcherTask` (patrz `.csproj`) — bez niego plik
`.exe.config` nie jest poprawnie generowany i `GetConnectionStatus()` **zawsze
zwraca false**, nawet gdy Tekla działa z otwartym modelem. To architektura
„gacless" w Tekli 2024/2025.

## Przepływ jednego kliknięcia

```
MainForm.RunButton_Click
  └─ RadiusDimensionService.AutoPlaceWithCollisionAvoidance(log)
       │
       ├─ 1. połączenie + pobranie aktywnego rysunku
       ├─ 2. jedno przejście po arkuszu: zbierz RadiusDimension[] i Mark[]
       │
       ├─ 3. TightenMarks(...)                    ← etap 1
       │      dla każdego opisu: PlacingAttributes = auto z ciasnym zakresem
       │    CommitChanges()
       │
       ├─ 4. dla każdego wymiaru R:               ← etap 2
       │      TryPlaceByGeometry(...)  →  zwraca LeaderRay
       │        ├─ CircumCenter(ArcPoint1,2,3) → środek + promień
       │        ├─ GetPartFacts(...)  → wymiary płaszczyzny + liczba otworów
       │        ├─ decyzja: wewnątrz czy na zewnątrz
       │        └─ Attributes.Placing = Fixed; Distance = ±wyliczona; Modify()
       │      (jeśli null → PlaceUsingFreeMode jako wariant awaryjny)
       │    CommitChanges()
       │
       └─ 5. NudgeMarksOffLeaders(marks, leaderRays)   ← etap 3
              dla każdego opisu: rzut na promień leadera,
              jeśli za blisko → przesuń prostopadle o brakującą różnicę
            CommitChanges()
```

**Trzy etapy, każdy to jedno przejście po obiektach i jeden `CommitChanges()`.**
Kolejność nie jest przypadkowa:

- opisy trzeba **najpierw** dociągnąć (inaczej wiszą daleko i etap 3 liczyłby
  kolizje dla nieaktualnych pozycji),
- wymiary R muszą być ustawione **przed** etapem 3, bo dopiero wtedy znamy
  promienie linii odniesienia,
- odsuwanie opisów jest **na końcu**, bo to korekta względem gotowych wymiarów.

## Klasy pomocnicze w serwisie

| Typ | Rola |
|---|---|
| `MoveResult` | Wynik dla UI: ile wymiarów znaleziono, ile ustawiono. |
| `LeaderRay` (prywatna) | Półprosta linii odniesienia: punkt startowy na łuku, kierunek, długość do sprawdzania kolizji. |
| `PartFacts` (prywatna) | Dane części z modelu: `FaceLongMm`, `FaceShortMm`, `BoltCount`, `BooleanCount`, `Valid`. |

## Stan i UI

Program **nie trzyma żadnego stanu** między kliknięciami — nie ma historii
cofania ani śledzenia „na którym rysunku już przesuwano".

- **Cofanie**: zwykłe **Ctrl+Z** w Tekli.
- **Przycisk jest zawsze aktywny.** Wcześniej blokował się po przesunięciu, ale
  Tekla nie zgłasza zdarzenia przy Ctrl+Z, więc po cofnięciu przycisk zostawał
  szary i nie było jak przesunąć ponownie. Ponowne przesunięcie jest
  nieszkodliwe (wymiary są liczone od nowa z tych samych danych).
- **Podpis pod przyciskiem** pokazuje aktualnie otwarty rysunek. Aktualizuje go
  `RefreshState()` wołane ze zdarzeń Tekli.

## Nasłuch zdarzeń Tekli

Wzorzec przejęty z projektu `HFT_Organizer_Mostowy` (`UI/MainForm.cs`,
`RegisterTeklaEvents`):

```csharp
_drawingEvents = new Tekla.Structures.Drawing.UI.Events();
_drawingEvents.DrawingLoaded        += OnTeklaContextChanged;  // wejście na inny rysunek
_drawingEvents.DrawingEditorOpened  += OnTeklaContextChanged;
_drawingEvents.DrawingEditorClosed  += OnTeklaContextChanged;
_drawingEvents.Register();

_modelEvents = new Tekla.Structures.Model.Events();
_modelEvents.TeklaStructuresExit += OnTeklaExited;
_modelEvents.Register();
```

Dwie rzeczy, o których łatwo zapomnieć:

1. **Zdarzenia przychodzą z wątku Tekli** — do UI trzeba wrócić przez
   `BeginInvoke` (`UiInvoke()` w `MainForm`). Bez tego ruszanie kontrolkami
   rzuci wyjątkiem.
2. **Po utracie połączenia trzeba wyrejestrować zdarzenia** — rejestracje
   wskazują na nieistniejący proces i po ponownym starcie Tekli program nigdy
   nie dostanie już żadnego zdarzenia. Dlatego `TryConnectAndWatch()` woła
   `UnregisterTeklaEvents()` i wznawia timer ponawiający próbę.

Timer (`_connectRetryTimer`, 3 s) chodzi **tylko dopóki nie ma połączenia** —
po podłączeniu jest zatrzymywany, żeby nie pingować Tekli bez potrzeby.
