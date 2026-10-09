# Videojukebox på Debian 13

Programmet er én fil: `videojukebox.deb`. Den kopieres som en almindelig fil. Den skal ikke skrives med Etcher, og den sletter ikke disken.

## På maskinen, når hylden kører

Hold fingeren på klubnavnet i to sekunder, og tast **2580**. Gæster ser kun filmene. Tryk **Hylden** for at komme tilbage.

Når koden er tastet, kan en ny `videojukebox.deb` på en USB-nøgle lægges på under **Opdatering**.

## Første gang, hvis skrivebordet stadig er fremme

1. Kopiér `videojukebox.deb` over på en USB-nøgle.
2. Sæt nøglen i Debian-computeren.
3. Åbn nøglen, og dobbeltklik på `videojukebox.deb`.
4. Tryk Installér.
5. Lad netværkskablet sidde i. Genstart, når installationen er færdig.

Netværk og Onlime sættes op under Administration. Mangler Onlime Sync, så sæt kablet i, åbn **Onlime**, og tryk **Hent Onlime Sync**. Log derefter ind, og kopiér koden. Film lægges i `media`, plakater i `events`.

Pakken bygges med `./deploy/scripts/build-deb.sh` og lander i `publish/pakke/videojukebox.deb`.

Operativsystemet, hvis det endnu ikke er installeret, er Debian 13.7 til 64-bit pc: [debian-13.7.0-amd64-netinst.iso](https://get.debian.org/images/release/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso). Under programvalg beholdes skrivebordet, så filen kan åbnes med et dobbeltklik.
