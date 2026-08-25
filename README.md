# Radius Dimension Mover – Tekla Structures 2025

Osobna aplikacja (.exe) z jednym przyciskiem: przesuwa **wszystkie wymiary R**
na aktywnym rysunku na zewnątrz elementu, żeby tekst nie wpadał w kontur rysunku.

## Jak to działa

1. Aplikacja łączy się z uruchomioną Teklą przez Tekla Open API (`DrawingHandler`),
   dokładnie tak samo jak zwykłe makra Tekli – różnica jest tylko taka, że to
   osobny plik .exe, a nie skrypt uruchamiany z wnętrza Tekli.
2. Bierze aktualnie otwarty rysunek (`GetActiveDrawing()`).
3. Przechodzi po wszystkich obiektach na arkuszu i wybiera te typu `RadiusDimension`.
4. Dla każdego z nich liczy kierunek od środka promienia do punktu wymiaru
   i przesuwa ten punkt dalej w tym samym kierunku o wpisaną w aplikacji
   odległość w mm (domyślnie 100 mm).
5. Zapisuje zmiany (`Modify()` na każdym wymiarze + `CommitChanges()` na rysunku).

## Wymagania

- Tekla Structures 2025 zainstalowana na tym samym komputerze.
- Visual Studio (2019/2022) z obciążeniem ".NET desktop development".
- .NET Framework 4.8 Developer Pack.

## Budowanie

1. Otwórz `RadiusDimensionMover.csproj` w Visual Studio.
2. W pliku `.csproj` popraw ścieżki `HintPath` do trzech referencji Tekla,
   tak żeby wskazywały na Twój faktyczny folder instalacji, np.:
   ```
   C:\Program Files\Tekla Structures\2025.0\nt\bin\plugins\
   ```
3. Zbuduj projekt (Build → Build Solution). Powinien powstać
   `RadiusDimensionMover.exe`.

**Jeśli kompilacja zgłosi błąd przy `CenterPoint`, `Point` albo `Modify()`**
w pliku `RadiusDimensionService.cs` – zajrzyj do lokalnej dokumentacji API
(Tekla → Help → Tekla Open API Reference, albo plik `.chm` w folderze
instalacji Tekli), znajdź klasę `RadiusDimension` i popraw nazwę właściwości.
Nie miałem tu środowiska Tekli, żeby to skompilować i przetestować 1:1 na
Twojej wersji – reszta logiki (pętla, wektor kierunku, offset) zostaje bez zmian.

## Uruchamianie

1. Uruchom Teklę, otwórz model, otwórz w edytorze rysunek pojedynczej części.
2. Uruchom `RadiusDimensionMover.exe` (może działać obok Tekli, niezależnie).
3. Ustaw odległość przesunięcia w mm.
4. Kliknij **"Przesuń wszystkie wymiary R"**.
5. Log w oknie pokaże ile wymiarów znaleziono i ile udało się przesunąć.
6. Wróć do Tekli – rysunek powinien odświeżyć się automatycznie
   (jeśli nie, zamknij i otwórz ponownie widok rysunku).

## Rozszerzenie: prawdziwa detekcja kolizji z innymi tekstami (v2, do dopracowania)

Obecna wersja robi "ślepe" przesunięcie o stałą wartość – rozwiązuje to
najczęstszy przypadek (wymiar R wpisany w środek rysunku). Żeby faktycznie
sprawdzać kolizje z konkretnymi innymi napisami, trzeba by:

1. Pobrać wszystkie obiekty tekstowe/wymiarowe na widoku
   (`Text`, `Dimension` i pochodne) i ich przybliżone bounding boxy.
2. Dla każdego `RadiusDimension` po przesunięciu sprawdzać, czy jego bounding
   box nachodzi na bounding box innego obiektu.
3. Jeśli tak – zwiększać offset iteracyjnie (np. co 20 mm, max np. 10 prób)
   aż kolizja zniknie albo skończą się próby.

To wymaga sprawdzenia w API Reference, jak dokładnie pobrać bounding box
danego typu obiektu w Tekla Open API (różni się to trochę między typami
obiektów) – nie chciałem zgadywać tych nazw na sucho, żeby nie wrzucać kodu,
który się nie skompiluje. Jeśli chcesz, mogę to dopisać w kolejnym kroku,
najlepiej z Tobą sprawdzającym nazwy metod w Object Browser w Visual Studio
(IntelliSense na żywym API pokaże dokładne sygnatury).
