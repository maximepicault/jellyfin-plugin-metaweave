# Catalogue of the fake API: series → seasons (sources) → videos; present = files created on disk
OV = "Dans cet épisode, l'équipe part en reportage au cœur des Alpes pour suivre une expédition hivernale qui tourne mal."
SERIES = {
  "aventuriers": {"title": "Les Aventuriers", "color": (41,128,185), "seasons": {
      "a1": {"title": "Saison 2024", "present": 3, "videos": [("Le départ","2024-09-04","21:30"),("Premier bivouac","2024-09-11",1500),("La crevasse","2024-09-18","25:10"),("Le col &amp; la crevasse d&#39;été","2024-10-02","21:00"),("Retour au camp","2024-10-09","23:05"),("Tempête","2024-10-16",754),("Épilogue","2024-10-23","1:02:03")]},
      "a2": {"title": "Hors-série", "present": 1, "videos": [("Les coulisses","2024-05-01","9:00"),("Making-of","2024-06-01","PT14M30S")]},
      "a3": {"title": "Saison 2023", "present": 2, "videos": [("Pilote","2023-03-01","20:00"),("Finale","2023-04-01","22:00")]}}},
  "cuisine": {"title": "Cuisine du monde", "color": (211,84,0), "seasons": {
      "b1": {"title": "Saison 1", "present": 1, "nonumbers": True, "videos": [("L'Italie","2025-01-05","28:40"),("Le Japon","2025-01-12","30:00"),("Le Pérou","2025-01-19","29:10"),("Le Maroc","2025-01-26","abc")]}}},
  "techtalks": {"title": "Tech Talks", "color": (142,68,173), "seasons": {
      "d1": {"title": "Conférences 2025", "present": 2, "videos": [("Talk #%d" % i, "2025-%02d-15" % (1 + i % 12), "%d:%02d" % (38 + i, i * 5 % 60)) for i in range(1, 11)]}}},
  "nature": {"title": "Documentaires Nature", "color": (39,174,96), "seasons": {
      "e1": {"title": "Océans", "present": 1, "broken": True, "videos": [("Les abysses","2025-02-02","52:00"),("Les récifs","2025-02-09","51:00")]}}},
}

# People of the fake API (people source, prefix "pe"): p3 has the same name as p1 (deduplication by name)
PEOPLE = {"p1": ("Camille Durand", "Comédienne et animatrice, à l'antenne depuis 2015."),
          "p2": ("Émile Lefèvre", "Réalisateur &amp; scénariste."),
          "p3": ("camille  DURAND", "Même personne, autre API.")}

# Demo "dump": ONE call returns the whole catalogue of standalone episodes ({"data": [...]}), as a real dump source does.
# The first entries are the targets of tools/ui-test/dump-real.mjs (see gen.py for the files that match them).
_DUMP_BRANDS = ["Aurora Studio", "Boréal Films", "Cinéma Lumen", "Delta Prod"]
_DUMP_FIXED = [
    (1001, "Voyage au bout de la nuit", 0, "voyage au bout de la nuit", ["aventure", "nuit"]),
    (1002, "Les Sentiers du Nord", 0, "les sentiers du nordthe northern pathsdie nordpfade", ["nature", "voyage"]),
    (1003, "Rivages oubliés", 1, "rivages oublies", ["mer", "nature"]),
    (1004, "Chroniques de la forêt 1", 1, "chroniques de la foret 1", ["nature", "forêt"]),
    (1005, "Chroniques de la forêt 2", 1, "chroniques de la foret 2", ["nature", "forêt"]),
    (1006, "Le Dernier Phare", 2, "le dernier pharethe last lighthousedas letzte leuchtfeuer", ["mer", "drame"]),
    (1007, "Un titre &amp; son <b>secret</b>", 2, "un titre et son secret", ["drame"]),
]
DUMP = []
for _id, _name, _brand, _search, _tags in _DUMP_FIXED:
    DUMP.append((_id, _name, _brand, _search, _tags))
for _i in range(8, 41):
    DUMP.append((1000 + _i, "Épisode démo %d" % _i, _i % 4, "episode demo %d" % _i, [["aventure", "drame", "nature", "voyage"][_i % 4], "démo"]))
