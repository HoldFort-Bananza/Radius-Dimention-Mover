# Radius Dimension Mover – Tekla Structures 2025

Osobna aplikacja (.exe) do szybkiego przesuwania **wszystkich wymiarów R**
na aktywnym rysunku na zewnątrz elementu, żeby tekst nie wpadał w kontur
rysunku ani w inne teksty/wymiary.

## Jak to działa

1. Aplikacja łączy się z uruchomioną Teklą przez Tekla Open API (`DrawingHandler`),
   dokładnie tak samo jak zwykłe makra Tekli – różnica jest tylko taka, że to
   osobny plik .exe, a nie skrypt uruchamiany z wnętrza Tekli.
2. Bierze aktualnie otwarty rysunek (`GetActiveDrawing()`).
3. Przechodzi po wszystkich obiektach na arkuszu i wybiera te typu `RadiusDimension`
   i `Mark` (opisy typu "1*Ø13" ze śrub/otworów, z leaderem).
4. Najpierw dociąga opisy (`Mark`) bliżej tego, co opisują - ustawia
   `Attributes.PlacingAttributes` na tryb auto (`IsFixed=false`) z ciasnym
   zakresem szukania 10-60mm na papierze, zamiast domyślnego "bez limitu",
   który potrafił wyrzucić opis bardzo daleko.
5. Dla każdego wymiaru R liczy kierunek "na zewnątrz części" **z geometrii
   łuku** (`ArcPoint1/2/3` → środek okręgu; dla wypukłego zaokrąglenia
   narożnika środek leży po stronie materiału, więc kierunek "od środka na
   zewnątrz" = kierunek "od materiału"), sprawdza jednym zrzutem ekranu, czy
   znak `+`/`-` w Tekli zgadza się z tym kierunkiem, po czym w trybie
   `Fixed` szuka wolnej (nienachodzącej na inne elementy) odległości wzdłuż
   TEJ potwierdzonej strony (15-300mm na papierze, krok 15mm). Jeśli z
   jakiegokolwiek powodu (np. zdegenerowana geometria, okno Tekli nie
   znalezione) nie da się tego ustalić, wymiar spada do wbudowanego w Teklę
   trybu **`Placing=Free`** jako bezpiecznego wariantu awaryjnego (dobrze
   unika kolizji z innymi elementami, ale sam z siebie potrafi wylądować
   wewnątrz konturu części - stąd potrzeba głównej metody powyżej).
6. Zapisuje zmiany (`Modify()` na każdym obiekcie + `CommitChanges()` na rysunku).

Wcześniejsze podejścia okazały się zawodne albo kruche: `RadiusDimension` nie
ma żadnego sposobu odczytania własnej POZYCJI na rysunku, a wbudowany w Teklę
tryb `Placing=Free` (wybiera wolne miejsce, unikając kolizji z innymi
tekstami/wymiarami) ma martwy punkt - kąt/stronę, po której ląduje, wybiera
sam, "na sztywno" per wymiar, i żaden atrybut (`Direction`
Positive/Negative) tego nie zmienia (sprawdzone empirycznie - 3 niezależne
testy dały identyczny wynik), więc czasem ląduje w środku konturu części.
Próba wykrycia konturu części piksel po pikselu (analiza "białych" pikseli na
zrzucie ekranu) też zawiodła - linie i strzałki wymiarowe są rysowane tym
samym kolorem co krawędzie części, więc nie da się ich odróżnić samą
analizą koloru. Zadziałało dopiero policzenie kierunku wprost z geometrii
łuku (`ArcPoint1/2/3`, dostępne przez API w tych samych jednostkach co
`Distance`) połączone z jednym zrzutem ekranu tylko po to, żeby ustalić
konwencję znaku Tekli - patrz `RadiusDimensionService.TryPlaceOutside`.

## Bezpieczniki

- **Blokada po przesunięciu**: po udanym kliknięciu "Przesuń" przycisk się
  blokuje, dopóki nie klikniesz "Cofnij" – chroni przed przypadkowym
  wielokrotnym klikaniem (np. gdy Tekla na chwilę nie odpowiada).
- **Wykrywanie ręcznej zmiany**: jeśli po przesunięciu ręcznie poprawisz
  pozycję wymiaru w Tekli (albo otworzysz inny rysunek), program to zauważy
  przy powrocie do okna (fokus okna) i sam odblokuje przycisk "Przesuń".
- **Cofnij**: przywraca oryginalne `Attributes` (w tym tryb Placing) wymiarów
  R i opisów sprzed ostatniego kliknięcia "Przesuń", krok po kroku.
- **Log sesji**: każda sesja programu zapisuje pełny log do pliku w
  `logs\session_<data_godzina>.log` obok pliku .exe.

## Wymagania

- Tekla Structures 2025 zainstalowana na tym samym komputerze (z ważną licencją).
- Internet podczas instalacji (instalator pobiera biblioteki Tekla Open API).

## Instalacja

1. Pobierz `RadiusDimensionMover-Setup-vX.Y.exe` z [Releases](../../releases).
2. Uruchom go i zaakceptuj pokazaną licencję (EULA Trimble/Tekla).
3. Instalator sam pobierze wymagane biblioteki Tekla Open API świeżo z
   publicznego NuGet (nuget.org) - pod Twoją własną licencją Tekli, dokładnie
   to samo co zrobiłby `dotnet restore`, tylko zautomatyzowane. Te biblioteki
   **nie są dołączone do repo/instalatora** - są własnością Trimble/Tekla i
   ich licencja zabrania redystrybucji stronom trzecim, a to repo jest
   publiczne. Instalator tylko automatyzuje ich legalne pobranie na Twój
   komputer.
4. Gotowe - skrót w Menu Start (i opcjonalnie na pulpicie).

Źródła instalatora (w pełni jawne, nic ukrytego) są w folderze `installer/`:
`setup.iss` (Inno Setup) i `fetch-dependencies.ps1` (skrypt pobierający
biblioteki).

## Uruchamianie

1. Uruchom Teklę, otwórz model, otwórz w edytorze rysunek pojedynczej części.
2. Uruchom Radius Dimension Mover (skrót z Menu Start/pulpitu).
3. Kliknij **"Przesuń wszystkie wymiary R (unikaj kolizji)"** - jeden
   przycisk, bez żadnych parametrów do wpisywania.
4. Log w oknie pokaże ile wymiarów znaleziono i ile udało się rozstawić.
5. Wróć do Tekli i sprawdź wzrokowo. Jeśli coś jest nie tak, kliknij
   "Cofnij", żeby wrócić do stanu sprzed operacji.

## Budowanie z kodu źródłowego (dla programistów)

1. Otwórz `RadiusDimensionMover.csproj` w Visual Studio (albo `dotnet build
   RadiusDimensionMover.csproj -c Debug -p:Platform=x64`).
2. NuGet automatycznie pobierze pakiety `Tekla.Structures*` w wersji 2025.0.0.
   Jeśli masz inną wersję Tekli, popraw wersję pakietów w `.csproj`.
3. Zbuduj projekt. Powstanie `bin\x64\Debug\net48\RadiusDimensionMover.exe`.

Żeby zbudować sam instalator: zainstaluj [Inno Setup](https://jrsoftware.org/isinfo.php),
zbuduj najpierw projekt jak wyżej, potem uruchom `ISCC.exe installer\setup.iss`.
