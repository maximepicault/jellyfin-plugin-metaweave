# Mémoire du projet — format

Ces fichiers sont écrits pour être lus par Claude : texte brut structuré, identifiants stables, liens explicites, pas de schéma. Git garde le *quoi* ; ici on garde le *pourquoi* et *où on en est*. Aucun secret, mot de passe ni token.

## Fichiers
- ETAT.md : état courant, max ~40 lignes.
- DECISIONS.md : index en tête (une ligne par décision), puis le détail.
- PISTES-ECARTEES.md : ce qui a été essayé ou envisagé puis abandonné, et pourquoi.
- archive-AAAA.md : anciennes décisions remplacées ou abandonnées, déplacées hors de l'index.

## Identifiants
D001, D002... pour les décisions ; P001, P002... pour les pistes. Séquentiels, jamais réutilisés.

## Index de DECISIONS.md (la plus récente en haut)
ID | statut | titre | liens
Statuts : active, remplacée par D0xx, abandonnée.
Vocabulaire des liens, toujours ces termes exacts : dépend de, remplace, remplacée par, invalide, motivée par (pour une piste P0xx). Un lien doit exister dans l'index ET dans le détail, et correspondre à une vraie relation.

## Détail d'une décision
## D012 — Titre court
- Date : AAAA-MM-JJ
- Contexte : pourquoi la question se posait
- Décision : ce qui a été choisi
- Alternatives écartées : option → raison
- Conséquences : ce que ça impose ou interdit
- Liens : dépend de D004 ; motivée par P003
- Code : chemins ou commits concernés (ancrage vérifiable)
- À réévaluer si : condition qui remettrait la décision en cause
- Fiabilité : étiqueter les faits clés [vérifié] (mesuré, testé, lu dans la source) ou [supposé]

## Piste écartée (PISTES-ECARTEES.md)
P003 — AAAA-MM-JJ — Idée : raison de l'abandon (fichier ou commit si utile)

## Tailles
ETAT.md > ~40 lignes ou index > ~60 lignes : déplacer les décisions remplacées ou abandonnées les plus anciennes, avec leur détail, dans archive-AAAA.md ; ne laisser qu'une ligne de renvoi dans l'index.
