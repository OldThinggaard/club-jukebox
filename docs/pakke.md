# Videojukebox på Debian 13

Når hylden allerede kører, og der ikke er noget at trykke på, lægges en ny udgave på med filen `videojukebox-opdater.iso`.

1. Hent filen på den computer, hvor du plejer at hente filer.
2. Læg den på en USB-nøgle med Etcher. Viser Etcher «Error opening source», så brug Rufus og vælg DD.
3. Sluk jukebox-computeren, sæt nøglen i, og tænd.
4. Kommer hylden frem med det samme, så sluk, tænd, tryk F12 én gang mens skærmen er sort, og vælg USB-nøglen.
5. Skærmen skriver, at den opdaterer. Vent. Den genstarter selv.
6. Tag nøglen ud, når hylden er tilbage.
7. Hold fingeren på klubnavnet i to sekunder, og tast 2580.

Nøglen sletter ikke disken. Gæster ser kun filmene.

Debian 13 skal allerede være installeret. Programmet lægges på med én fil: `videojukebox.deb`. Filen pakkes ikke ud, og den sletter ikke disken.

## På maskinen, når hylden kører

Hold fingeren på klubnavnet i to sekunder, og tast **2580**. Gæster ser kun filmene. Tryk **Hylden** for at komme tilbage.

Når koden er tastet, kan en ny `videojukebox.deb` på en USB-nøgle lægges på under **Opdatering**.

## Første gang, hvis skrivebordet stadig er fremme

1. Kopiér `videojukebox.deb` over på en USB-nøgle.
2. Sæt nøglen i Debian-computeren.
3. Åbn nøglen, og dobbeltklik på `videojukebox.deb`.
4. Tryk Installér.
5. Lad netværkskablet sidde i. Genstart, når installationen er færdig.

Netværk og Onlime sættes op under Administration. Film lægges i `media`, plakater i `events`.

Pakken bygges med `./deploy/scripts/build-deb.sh` og lander i `publish/pakke/videojukebox.deb`.

Operativsystemet, hvis det endnu ikke er installeret, er Debian 13.7 til 64-bit pc: [debian-13.7.0-amd64-netinst.iso](https://get.debian.org/images/release/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso). Under programvalg beholdes skrivebordet, så filen kan åbnes med et dobbeltklik.
