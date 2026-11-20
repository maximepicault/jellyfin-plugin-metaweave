---
name: memoire-audit
description: Auditer la cohérence de docs/memoire/ : index vs détail, liens, ancrages dans le code, conditions de réévaluation, faits non vérifiés, tailles. À utiliser périodiquement ou quand le journal semble douteux.
---

# Audit de la mémoire de projet

Format de référence : `docs/memoire/README.md`. L'audit **rapporte** ; il ne réécrit pas le fond des décisions.

## Contrôles

1. **Index et détail** : chaque ID de l'index a un détail, et inversement ; statut, titre et liens identiques des deux côtés.
2. **Liens** : chaque lien pointe vers un ID existant ; `remplace` et `remplacée par` sont réciproques ; pas d'ID dupliqué ni réutilisé.
3. **Ancrages `Code`** : les chemins cités existent encore (`test -e`) ; signaler ceux dont le contenu a fortement changé depuis la date de la décision (`git log --since`).
4. **`À réévaluer si`** : pour chaque décision active, la condition semble-t-elle remplie d'après l'état du code ou d'`ETAT.md` ?
5. **Fiabilité** : lister les faits `[supposé]` qui portent une décision active, surtout s'ils datent de plus de 30 jours.
6. **Décisions actives sans `Code` ni `À réévaluer si`** : signaler comme information, pas comme erreur.
7. **Tailles** : `ETAT.md` > ~40 lignes, index > ~60 lignes → recommander l'archivage.
8. **Contradictions** avec le code réel : signaler, ne pas trancher à la place de l'utilisateur.

## Sortie

Un rapport court, trié par gravité :
- **Erreurs** (incohérences mécaniques, liens cassés, ancrages disparus) ;
- **À revoir** (conditions remplies, faits supposés, décisions peut-être périmées) ;
- **Infos**.

Pour chaque point : l'ID concerné, ce qui a été constaté, la correction proposée.

## Règles

- Ne corriger que les incohérences purement mécaniques (index ≠ détail), et seulement si l'ordre d'appliquer a été donné ; tout le reste est proposé, jamais imposé.
- Ne pas supprimer de décision : une décision obsolète passe au statut `remplacée` ou `abandonnée`.
