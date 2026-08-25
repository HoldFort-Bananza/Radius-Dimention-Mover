# Radius Dimension Mover – Tekla Structures 2025

Osobna aplikacja (.exe) do szybkiego przesuwania **wszystkich wymiarów R**
na aktywnym rysunku na zewnątrz elementu, żeby tekst nie wpadał w kontur
rysunku ani w inne teksty/wymiary.

## Jak to działa

1. Aplikacja łączy się z uruchomioną Teklą przez Tekla Open API (`DrawingHandler`),
   dokładnie tak samo jak zwykłe makra Tekli – różnica jest tylko taka, że to
   osobny plik .exe, a nie skrypt uruchamiany z wnętrza Tekli.
2. Bierze aktualnie otwarty rysunek (`GetActiveDrawing()`).
3. Przechodzi po wszystkich obiektach na arkuszu i wybiera te typu `RadiusDimension`.
4. Dla każdego z nich zwiększa `Distance` o wpisany krok (mm **na papierze** –
   przeliczane przez skalę widoku, `View.Attributes.Scale`, więc krok znaczy
   to samo niezależnie od skali rysunku) w kierunku, który Tekla sama wybiera.
5. Zapisuje zmiany (`Modify()` na każdym wymiarze + `CommitChanges()` na rysunku).

Program **nie próbuje automatycznie omijać kolizji** z innymi tekstami czy
wymiarami – Tekla Open API nie udostępnia żadnego sposobu odczytania ani
przewidzenia rzeczywistej pozycji tekstu wymiaru R (`ArcPoint1/2/3` są stałe
niezależnie od `Distance` – sprawdzone empirycznie), więc nie da się tego
zbudować wiarygodnie. Klikasz "Przesuń", oceniasz wzrokowo w Tekli, i jeśli
trzeba – "Cofnij" i próbujesz ponownie z innym krokiem.

## Bezpieczniki

- **Blokada po przesunięciu**: po udanym kliknięciu "Przesuń" przycisk się
  blokuje, dopóki nie klikniesz "Cofnij" – chroni przed przypadkowym
  wielokrotnym klikaniem (np. gdy Tekla na chwilę nie odpowiada), które
  wypchnęłoby wymiar dużo dalej niż zamierzone.
- **Wykrywanie ręcznej zmiany**: jeśli po przesunięciu ręcznie poprawisz
  pozycję wymiaru w Tekli, program to zauważy przy powrocie do okna (fokus
  okna) i sam odblokuje przycisk "Przesuń".
- **Cofnij**: przywraca oryginalną wartość `Distance` sprzed ostatniego
  kliknięcia "Przesuń", krok po kroku.
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
3. Ustaw krok w mm (domyślnie 40mm).
4. Kliknij **"Przesuń wszystkie wymiary R (+krok)"**.
5. Log w oknie pokaże ile wymiarów znaleziono i ile udało się przesunąć.
6. Wróć do Tekli i oceń wzrokowo – jeśli krok nie wystarczył, kliknij
   "Cofnij" i spróbuj ponownie z innym krokiem.

## Budowanie z kodu źródłowego (dla programistów)

1. Otwórz `RadiusDimensionMover.csproj` w Visual Studio (albo `dotnet build
   RadiusDimensionMover.csproj -c Debug -p:Platform=x64`).
2. NuGet automatycznie pobierze pakiety `Tekla.Structures*` w wersji 2025.0.0.
   Jeśli masz inną wersję Tekli, popraw wersję pakietów w `.csproj`.
3. Zbuduj projekt. Powstanie `bin\x64\Debug\net48\RadiusDimensionMover.exe`.

Żeby zbudować sam instalator: zainstaluj [Inno Setup](https://jrsoftware.org/isinfo.php),
zbuduj najpierw projekt jak wyżej, potem uruchom `ISCC.exe installer\setup.iss`.
