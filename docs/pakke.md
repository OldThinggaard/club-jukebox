# Videojukebox på Debian 13

Debian 13 skal allerede være installeret. Programmet lægges på med én fil: `videojukebox.deb`. Filen pakkes ikke ud, og den sletter ikke disken.

## På maskinen, når hylden kører

1. Tryk **Admin** på hylden.
2. Tast **2580**.
3. Tryk **Hylden** for at komme tilbage til filmene.

En ny version: kopiér `videojukebox.deb` til en USB-nøgle, sæt den i, tryk Admin, åbn **Opdatering**, og tryk **Installer og genstart**. Der skal ikke skrives noget.

## Første gang, hvis skrivebordet stadig er fremme

1. Kopiér `videojukebox.deb` over på en USB-nøgle.
2. Sæt nøglen i Debian-computeren.
3. Åbn nøglen, og dobbeltklik på `videojukebox.deb`.
4. Tryk Installér.
5. Lad netværkskablet sidde i. Genstart, når installationen er færdig.

Netværk og Onlime sættes op under Administration. Film lægges i `media`, plakater i `events`.

Pakken bygges med `./deploy/scripts/build-deb.sh` og lander i `publish/pakke/videojukebox.deb`.

Operativsystemet, hvis det endnu ikke er installeret, er Debian 13.7 til 64-bit pc: [debian-13.7.0-amd64-netinst.iso](https://get.debian.org/images/release/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso). Under programvalg beholdes skrivebordet, så filen kan åbnes med et dobbeltklik.
