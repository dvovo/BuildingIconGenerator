// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Localization
{
    using System.Collections.Generic;
    using BuildingIconGenerator.Settings;
    using Colossal;
    using Game.SceneFlow;

    /// <summary>
    /// Every user-facing string of the mod (options page and progress notifications), in the twelve
    /// languages the game ships with. The game picks the set matching its current language, so the
    /// mod follows the language selected in the game's options, also when it is changed at run time.
    /// Locales added by other mods use English.
    ///
    /// Option keys are produced by <see cref="Game.Modding.ModSetting"/>'s own helpers, so they always
    /// match what the options UI looks up.
    /// </summary>
    internal static class ModLocale
    {
        public const string kProgressTitle = Mod.Id + ".Progress.Title";
        public const string kProgressText = Mod.Id + ".Progress.Text";
        public const string kFinishedTitle = Mod.Id + ".Finished.Title";
        public const string kFinishedText = Mod.Id + ".Finished.Text";
        public const string kCleanupTitle = Mod.Id + ".Cleanup.Title";
        public const string kCleanupText = Mod.Id + ".Cleanup.Text";

        private const string kFallbackLocale = "en-US";

        /// <summary>Adds the mod's strings to the localization manager.</summary>
        public static void Register(ModSettings settings)
        {
            var manager = GameManager.instance?.localizationManager;

            if (manager == null)
            {
                return;
            }

            var translations = Translations();
            var locales = new HashSet<string>(manager.GetSupportedLocales());
            locales.UnionWith(translations.Keys);

            foreach (var locale in locales)
            {
                if (!translations.TryGetValue(locale, out var texts))
                {
                    texts = translations[kFallbackLocale];
                }

                manager.AddSource(locale, new Source(Entries(settings, texts)));
            }
        }

        private static Dictionary<string, string> Entries(ModSettings s, Texts t)
        {
            return new Dictionary<string, string>
            {
                { s.GetSettingsLocaleID(), "Building Icon Generator" },
                { s.GetOptionTabLocaleID(ModSettings.kSection), t.Tab },
                { s.GetOptionGroupLocaleID(ModSettings.kGenerationGroup), t.GenerationGroup },
                { s.GetOptionGroupLocaleID(ModSettings.kIntegrationGroup), t.IntegrationGroup },
                { s.GetOptionGroupLocaleID(ModSettings.kMaintenanceGroup), t.MaintenanceGroup },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.GenerateAutomatically)), t.AutoLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.GenerateAutomatically)), t.AutoDesc },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.GameIcons)), t.ReplaceLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.GameIcons)), t.ReplaceDesc },
                { s.GetEnumValueLocaleID(GameIconScope.Off), t.IconsOff },
                { s.GetEnumValueLocaleID(GameIconScope.ZonedBuildings), t.IconsZoned },
                { s.GetEnumValueLocaleID(GameIconScope.AllBuildings), t.IconsAll },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.DeleteUnusedIcons)), t.CleanupLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.DeleteUnusedIcons)), t.CleanupDesc },
                { s.GetOptionWarningLocaleID(nameof(ModSettings.DeleteUnusedIcons)), t.CleanupWarning },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.GenerateNow)), t.NowLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.GenerateNow)), t.NowDesc },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.RegenerateAll)), t.AllLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.RegenerateAll)), t.AllDesc },
                { s.GetOptionWarningLocaleID(nameof(ModSettings.RegenerateAll)), t.AllWarning },

                { s.GetOptionLabelLocaleID(nameof(ModSettings.OpenIconsFolder)), t.FolderLabel },
                { s.GetOptionDescLocaleID(nameof(ModSettings.OpenIconsFolder)), t.FolderDesc },

                { kProgressTitle, t.ProgressTitle },
                { kProgressText, t.ProgressText },
                { kFinishedTitle, t.FinishedTitle },
                { kFinishedText, t.FinishedText },
                { kCleanupTitle, t.CleanupTitle },
                { kCleanupText, t.CleanupText },
            };
        }

        /// <summary>
        /// One set of strings per game locale. Notification texts keep the {DONE}, {TOTAL}, {COUNT}
        /// and {SECONDS} placeholders, which are filled in by the generator. Terms follow the game's own
        /// translations (thumbnail, folder, frames, options tab name), and counts are phrased so that no
        /// plural form is needed.
        /// </summary>
        private static Dictionary<string, Texts> Translations()
        {
            return new Dictionary<string, Texts>
            {
                ["en-US"] = new Texts
                {
                    Tab = "General",
                    GenerationGroup = "Generation",
                    IntegrationGroup = "Game integration",
                    MaintenanceGroup = "Maintenance",
                    AutoLabel = "Generate icons automatically",
                    AutoDesc = "Shortly after the game starts, renders an icon for every building that does not have one yet or has changed since its icon was made. Icons deleted from the folder are rendered again. The work is spread over several frames, so the menu stays responsive.",
                    ReplaceLabel = "Use the generated icons in the game",
                    ReplaceDesc = "Shows the generated icons in the game's interface instead of the default thumbnails: only for zoned and signature buildings, or for all buildings (also services, transport, parks, landmarks and upgrades). Off restores the original icons immediately. No game files are modified.",
                    NowLabel = "Generate missing icons now",
                    NowDesc = "Renders the missing and out-of-date icons now, without waiting for the next start.",
                    AllLabel = "Regenerate all icons",
                    AllDesc = "Renders every icon again, including those that are up to date.",
                    AllWarning = "All building icons will be rendered again. This may take a few minutes. Continue?",
                    FolderLabel = "Open icons folder",
                    FolderDesc = "Opens ModsData/BuildingIconGenerator/icons, which holds one PNG per building.",
                    ProgressTitle = "Generating building icons",
                    ProgressText = "{DONE} of {TOTAL}",
                    FinishedTitle = "Building icons ready",
                    FinishedText = "{COUNT} rendered in {SECONDS}s",
                    IconsOff = "Off",
                    IconsZoned = "Zoned and signature buildings",
                    IconsAll = "All buildings",
                    CleanupLabel = "Delete unused icons",
                    CleanupDesc = "Deletes the icons of buildings that are not installed right now, such as those of mods you have removed, and any other PNG in the icons folder that belongs to no building. If you install that content again, its icons are rendered again automatically.",
                    CleanupWarning = "The icons of all buildings that are not loaded right now will be deleted. Continue?",
                    CleanupTitle = "Unused icons deleted",
                    CleanupText = "Deleted: {COUNT}",
                },
                ["es-ES"] = new Texts
                {
                    Tab = "General",
                    GenerationGroup = "Generación",
                    IntegrationGroup = "Integración con el juego",
                    MaintenanceGroup = "Mantenimiento",
                    AutoLabel = "Generar iconos automáticamente",
                    AutoDesc = "Poco después de iniciar el juego, genera un icono para cada edificio que aún no lo tenga o que haya cambiado desde que se generó su icono. Los iconos eliminados de la carpeta se vuelven a generar. El trabajo se reparte entre varios fotogramas, así que el menú sigue respondiendo con fluidez.",
                    ReplaceLabel = "Usar los iconos generados en el juego",
                    ReplaceDesc = "Muestra los iconos generados en la interfaz del juego en lugar de las miniaturas predeterminadas: solo para los edificios de zona y emblemáticos, o para todos los edificios (también servicios, transporte, parques, monumentos y mejoras). Desactivado restaura al instante los iconos originales. No se modifica ningún archivo del juego.",
                    NowLabel = "Generar ahora los iconos que faltan",
                    NowDesc = "Genera ahora los iconos que faltan o están desactualizados, sin esperar al siguiente inicio.",
                    AllLabel = "Regenerar todos los iconos",
                    AllDesc = "Vuelve a generar todos los iconos, incluidos los que ya están actualizados.",
                    AllWarning = "Se volverán a generar todos los iconos de edificios. Puede tardar unos minutos. ¿Continuar?",
                    FolderLabel = "Abrir la carpeta de iconos",
                    FolderDesc = "Abre ModsData/BuildingIconGenerator/icons, que contiene un PNG por edificio.",
                    ProgressTitle = "Generando iconos de edificios",
                    ProgressText = "{DONE} de {TOTAL}",
                    FinishedTitle = "Iconos de edificios listos",
                    FinishedText = "Generados: {COUNT} en {SECONDS} s",
                    IconsOff = "Desactivado",
                    IconsZoned = "Edificios de zona y emblemáticos",
                    IconsAll = "Todos los edificios",
                    CleanupLabel = "Eliminar iconos sin usar",
                    CleanupDesc = "Elimina los iconos de los edificios que no están instalados ahora mismo, como los de mods que hayas quitado, y cualquier otro PNG de la carpeta de iconos que no pertenezca a ningún edificio. Si vuelves a instalar ese contenido, sus iconos se generan de nuevo automáticamente.",
                    CleanupWarning = "Se eliminarán los iconos de todos los edificios que no estén cargados ahora mismo. ¿Continuar?",
                    CleanupTitle = "Iconos sin usar eliminados",
                    CleanupText = "Eliminados: {COUNT}",
                },
                ["de-DE"] = new Texts
                {
                    Tab = "Allgemein",
                    GenerationGroup = "Erstellung",
                    IntegrationGroup = "Spielintegration",
                    MaintenanceGroup = "Wartung",
                    AutoLabel = "Symbole automatisch erstellen",
                    AutoDesc = "Erstellt kurz nach dem Spielstart ein Symbol für jedes Gebäude, das noch keines hat oder sich seit der Erstellung seines Symbols geändert hat. Aus dem Ordner gelöschte Symbole werden neu erstellt. Die Arbeit wird auf mehrere Frames verteilt, damit das Menü flüssig bedienbar bleibt.",
                    ReplaceLabel = "Erstellte Symbole im Spiel verwenden",
                    ReplaceDesc = "Zeigt die erstellten Symbole in der Benutzeroberfläche des Spiels anstelle der Standard-Miniaturansichten an: nur für Zonen- und Spezialgebäude oder für alle Gebäude (auch Dienstleistungen, Transport, Parks, Wahrzeichen und Aufwertungen). „Aus“ stellt die ursprünglichen Symbole sofort wieder her. Es werden keine Spieldateien verändert.",
                    NowLabel = "Fehlende Symbole jetzt erstellen",
                    NowDesc = "Erstellt fehlende und veraltete Symbole sofort, ohne auf den nächsten Spielstart zu warten.",
                    AllLabel = "Alle Symbole neu erstellen",
                    AllDesc = "Erstellt alle Symbole neu, auch die bereits aktuellen.",
                    AllWarning = "Alle Gebäudesymbole werden neu erstellt. Das kann einige Minuten dauern. Fortfahren?",
                    FolderLabel = "Symbolordner öffnen",
                    FolderDesc = "Öffnet ModsData/BuildingIconGenerator/icons, wo für jedes Gebäude eine PNG-Datei gespeichert ist.",
                    ProgressTitle = "Gebäudesymbole werden erstellt",
                    ProgressText = "{DONE} von {TOTAL}",
                    FinishedTitle = "Gebäudesymbole fertig",
                    FinishedText = "{COUNT} in {SECONDS} s erstellt",
                    IconsOff = "Aus",
                    IconsZoned = "Zonen- und Spezialgebäude",
                    IconsAll = "Alle Gebäude",
                    CleanupLabel = "Unbenutzte Symbole löschen",
                    CleanupDesc = "Löscht die Symbole von Gebäuden, die gerade nicht installiert sind, etwa aus entfernten Mods, sowie alle anderen PNG-Dateien im Symbolordner, die zu keinem Gebäude gehören. Werden diese Inhalte erneut installiert, werden ihre Symbole automatisch neu erstellt.",
                    CleanupWarning = "Die Symbole aller Gebäude, die gerade nicht geladen sind, werden gelöscht. Fortfahren?",
                    CleanupTitle = "Unbenutzte Symbole gelöscht",
                    CleanupText = "Gelöscht: {COUNT}",
                },
                ["fr-FR"] = new Texts
                {
                    Tab = "Général",
                    GenerationGroup = "Génération",
                    IntegrationGroup = "Intégration au jeu",
                    MaintenanceGroup = "Maintenance",
                    AutoLabel = "Générer les icônes automatiquement",
                    AutoDesc = "Peu après le lancement du jeu, génère une icône pour chaque bâtiment qui n'en a pas encore ou qui a changé depuis la création de son icône. Les icônes supprimées du dossier sont générées de nouveau. Le travail est réparti sur plusieurs images pour que le menu reste réactif.",
                    ReplaceLabel = "Utiliser les icônes générées en jeu",
                    ReplaceDesc = "Affiche les icônes générées dans l'interface du jeu à la place des vignettes par défaut\u00A0: uniquement pour les bâtiments zonés et emblématiques, ou pour tous les bâtiments (y compris services, transport, parcs, monuments et améliorations). «\u00A0Désactivé\u00A0» rétablit immédiatement les icônes d'origine. Aucun fichier du jeu n'est modifié.",
                    NowLabel = "Générer les icônes manquantes maintenant",
                    NowDesc = "Génère dès maintenant les icônes manquantes ou obsolètes, sans attendre le prochain lancement.",
                    AllLabel = "Régénérer toutes les icônes",
                    AllDesc = "Génère de nouveau toutes les icônes, y compris celles qui sont à jour.",
                    AllWarning = "Toutes les icônes de bâtiments vont être générées de nouveau. Cela peut prendre quelques minutes. Continuer\u00A0?",
                    FolderLabel = "Ouvrir le dossier des icônes",
                    FolderDesc = "Ouvre ModsData/BuildingIconGenerator/icons, qui contient un PNG par bâtiment.",
                    ProgressTitle = "Génération des icônes de bâtiments",
                    ProgressText = "{DONE} sur {TOTAL}",
                    FinishedTitle = "Icônes de bâtiments prêtes",
                    FinishedText = "Générées\u00A0: {COUNT} en {SECONDS}\u00A0s",
                    IconsOff = "Désactivé",
                    IconsZoned = "Bâtiments zonés et emblématiques",
                    IconsAll = "Tous les bâtiments",
                    CleanupLabel = "Supprimer les icônes inutilisées",
                    CleanupDesc = "Supprime les icônes des bâtiments qui ne sont pas installés actuellement, comme ceux des mods que vous avez retirés, ainsi que tout autre PNG du dossier des icônes qui n'appartient à aucun bâtiment. Si vous réinstallez ce contenu, ses icônes sont de nouveau générées automatiquement.",
                    CleanupWarning = "Les icônes de tous les bâtiments qui ne sont pas chargés actuellement vont être supprimées. Continuer\u00A0?",
                    CleanupTitle = "Icônes inutilisées supprimées",
                    CleanupText = "Supprimées\u00A0: {COUNT}",
                },
                ["it-IT"] = new Texts
                {
                    Tab = "Generale",
                    GenerationGroup = "Generazione",
                    IntegrationGroup = "Integrazione con il gioco",
                    MaintenanceGroup = "Manutenzione",
                    AutoLabel = "Genera icone automaticamente",
                    AutoDesc = "Poco dopo l'avvio del gioco, genera un'icona per ogni edificio che non ne ha ancora una o che è cambiato dalla creazione della sua icona. Le icone eliminate dalla cartella vengono generate di nuovo. Il lavoro è distribuito su più fotogrammi, così il menu resta reattivo.",
                    ReplaceLabel = "Usa le icone generate nel gioco",
                    ReplaceDesc = "Mostra le icone generate nell'interfaccia del gioco al posto delle miniature predefinite: solo per gli edifici di zona e distintivi, oppure per tutti gli edifici (anche servizi, trasporti, parchi, monumenti e potenziamenti). Con «Disattivato» le icone originali vengono ripristinate subito. Nessun file del gioco viene modificato.",
                    NowLabel = "Genera ora le icone mancanti",
                    NowDesc = "Genera subito le icone mancanti o non aggiornate, senza attendere il prossimo avvio.",
                    AllLabel = "Rigenera tutte le icone",
                    AllDesc = "Genera di nuovo tutte le icone, comprese quelle già aggiornate.",
                    AllWarning = "Tutte le icone degli edifici verranno generate di nuovo. L'operazione può richiedere alcuni minuti. Continuare?",
                    FolderLabel = "Apri la cartella delle icone",
                    FolderDesc = "Apre ModsData/BuildingIconGenerator/icons, che contiene un PNG per ogni edificio.",
                    ProgressTitle = "Generazione delle icone degli edifici",
                    ProgressText = "{DONE} di {TOTAL}",
                    FinishedTitle = "Icone degli edifici pronte",
                    FinishedText = "Icone generate: {COUNT} in {SECONDS} s",
                    IconsOff = "Disattivato",
                    IconsZoned = "Edifici di zona e distintivi",
                    IconsAll = "Tutti gli edifici",
                    CleanupLabel = "Elimina le icone inutilizzate",
                    CleanupDesc = "Elimina le icone degli edifici che al momento non sono installati, come quelli delle mod che hai rimosso, e qualsiasi altro PNG nella cartella delle icone che non appartiene a nessun edificio. Se reinstalli quei contenuti, le loro icone vengono generate di nuovo automaticamente.",
                    CleanupWarning = "Verranno eliminate le icone di tutti gli edifici che al momento non sono caricati. Continuare?",
                    CleanupTitle = "Icone inutilizzate eliminate",
                    CleanupText = "Eliminate: {COUNT}",
                },
                ["pt-BR"] = new Texts
                {
                    Tab = "Geral",
                    GenerationGroup = "Geração",
                    IntegrationGroup = "Integração com o jogo",
                    MaintenanceGroup = "Manutenção",
                    AutoLabel = "Gerar ícones automaticamente",
                    AutoDesc = "Logo após o jogo iniciar, gera um ícone para cada edifício que ainda não tem um ou que mudou desde a criação do seu ícone. Ícones excluídos da pasta são gerados novamente. O trabalho é distribuído em vários quadros, para que o menu continue respondendo normalmente.",
                    ReplaceLabel = "Usar os ícones gerados no jogo",
                    ReplaceDesc = "Mostra os ícones gerados na interface do jogo no lugar das miniaturas padrão: só para os edifícios com zoneamento e característicos, ou para todos os edifícios (também serviços, transporte, parques, pontos turísticos e melhorias). Desativado restaura os ícones originais imediatamente. Nenhum arquivo do jogo é modificado.",
                    NowLabel = "Gerar agora os ícones que faltam",
                    NowDesc = "Gera agora os ícones que faltam ou estão desatualizados, sem esperar a próxima inicialização.",
                    AllLabel = "Gerar todos os ícones novamente",
                    AllDesc = "Gera novamente todos os ícones, inclusive os que já estão atualizados.",
                    AllWarning = "Todos os ícones de edifícios serão gerados novamente. Isso pode levar alguns minutos. Continuar?",
                    FolderLabel = "Abrir pasta de ícones",
                    FolderDesc = "Abre ModsData/BuildingIconGenerator/icons, que contém um PNG para cada edifício.",
                    ProgressTitle = "Gerando ícones de edifícios",
                    ProgressText = "{DONE} de {TOTAL}",
                    FinishedTitle = "Ícones de edifícios prontos",
                    FinishedText = "Ícones gerados: {COUNT} em {SECONDS} s",
                    IconsOff = "Desativado",
                    IconsZoned = "Edifícios com zoneamento e característicos",
                    IconsAll = "Todos os edifícios",
                    CleanupLabel = "Excluir ícones não usados",
                    CleanupDesc = "Exclui os ícones dos edifícios que não estão instalados agora, como os de mods que você removeu, e qualquer outro PNG da pasta de ícones que não pertença a nenhum edifício. Se você instalar esse conteúdo de novo, os ícones dele são gerados novamente de forma automática.",
                    CleanupWarning = "Os ícones de todos os edifícios que não estão carregados agora serão excluídos. Continuar?",
                    CleanupTitle = "Ícones não usados excluídos",
                    CleanupText = "Excluídos: {COUNT}",
                },
                ["pl-PL"] = new Texts
                {
                    Tab = "Ogólne",
                    GenerationGroup = "Generowanie",
                    IntegrationGroup = "Integracja z grą",
                    MaintenanceGroup = "Konserwacja",
                    AutoLabel = "Generuj ikony automatycznie",
                    AutoDesc = "Krótko po uruchomieniu gry generuje ikonę dla każdego budynku, który jeszcze jej nie ma lub zmienił się od czasu jej utworzenia. Ikony usunięte z folderu są generowane ponownie. Praca jest rozłożona na wiele klatek, dzięki czemu menu działa płynnie.",
                    ReplaceLabel = "Używaj wygenerowanych ikon w grze",
                    ReplaceDesc = "Wyświetla wygenerowane ikony w interfejsie gry zamiast domyślnych miniatur: tylko dla budynków w strefach i charakterystycznych albo dla wszystkich budynków (także usług, transportu, parków, wyjątkowych obiektów i ulepszeń). Ustawienie „Wył.” natychmiast przywraca oryginalne ikony. Pliki gry nie są modyfikowane.",
                    NowLabel = "Wygeneruj teraz brakujące ikony",
                    NowDesc = "Od razu generuje brakujące lub nieaktualne ikony, bez czekania na następne uruchomienie gry.",
                    AllLabel = "Wygeneruj ponownie wszystkie ikony",
                    AllDesc = "Ponownie generuje wszystkie ikony, także te aktualne.",
                    AllWarning = "Wszystkie ikony budynków zostaną wygenerowane ponownie. Może to potrwać kilka minut. Kontynuować?",
                    FolderLabel = "Otwórz folder ikon",
                    FolderDesc = "Otwiera folder ModsData/BuildingIconGenerator/icons, w którym dla każdego budynku zapisywany jest jeden plik PNG.",
                    ProgressTitle = "Generowanie ikon budynków",
                    ProgressText = "{DONE} z {TOTAL}",
                    FinishedTitle = "Ikony budynków gotowe",
                    FinishedText = "Wygenerowano: {COUNT} w {SECONDS} s",
                    IconsOff = "Wył.",
                    IconsZoned = "Budynki w strefach i charakterystyczne",
                    IconsAll = "Wszystkie budynki",
                    CleanupLabel = "Usuń nieużywane ikony",
                    CleanupDesc = "Usuwa ikony budynków, które nie są teraz zainstalowane, np. z usuniętych modów, oraz wszystkie inne pliki PNG w folderze ikon, które nie należą do żadnego budynku. Po ponownej instalacji tej zawartości jej ikony zostaną automatycznie wygenerowane od nowa.",
                    CleanupWarning = "Ikony wszystkich budynków, które nie są teraz wczytane, zostaną usunięte. Kontynuować?",
                    CleanupTitle = "Usunięto nieużywane ikony",
                    CleanupText = "Usunięto: {COUNT}",
                },
                ["ru-RU"] = new Texts
                {
                    Tab = "Общее",
                    GenerationGroup = "Создание",
                    IntegrationGroup = "Интеграция с игрой",
                    MaintenanceGroup = "Обслуживание",
                    AutoLabel = "Создавать значки автоматически",
                    AutoDesc = "Вскоре после запуска игры создает значок для каждого здания, у которого его еще нет или которое изменилось с момента создания значка. Значки, удаленные из папки, создаются заново. Работа распределяется на несколько кадров, поэтому меню не теряет отзывчивости.",
                    ReplaceLabel = "Использовать созданные значки в игре",
                    ReplaceDesc = "Показывает созданные значки в интерфейсе игры вместо стандартных эскизов: только для зданий в зонах и уникальных зданий или для всех зданий (также службы, транспорт, парки, достопримечательности и улучшения). Вариант «Выкл.» сразу восстанавливает исходные значки. Файлы игры не изменяются.",
                    NowLabel = "Создать недостающие значки",
                    NowDesc = "Сразу создает недостающие и устаревшие значки, не дожидаясь следующего запуска игры.",
                    AllLabel = "Создать все значки заново",
                    AllDesc = "Создает заново все значки, включая актуальные.",
                    AllWarning = "Все значки зданий будут созданы заново. Это может занять несколько минут. Продолжить?",
                    FolderLabel = "Открыть папку значков",
                    FolderDesc = "Открывает папку ModsData/BuildingIconGenerator/icons, в которой хранится по одному PNG-файлу на каждое здание.",
                    ProgressTitle = "Создание значков зданий",
                    ProgressText = "{DONE} из {TOTAL}",
                    FinishedTitle = "Значки зданий готовы",
                    FinishedText = "Создано: {COUNT} за {SECONDS} с",
                    IconsOff = "Выкл.",
                    IconsZoned = "Здания в зонах и уникальные здания",
                    IconsAll = "Все здания",
                    CleanupLabel = "Удалить неиспользуемые значки",
                    CleanupDesc = "Удаляет значки зданий, которые сейчас не установлены (например, из удаленных модов), а также все остальные PNG-файлы в папке значков, не относящиеся ни к одному зданию. Если снова установить этот контент, его значки будут автоматически созданы заново.",
                    CleanupWarning = "Значки всех зданий, которые сейчас не загружены, будут удалены. Продолжить?",
                    CleanupTitle = "Неиспользуемые значки удалены",
                    CleanupText = "Удалено: {COUNT}",
                },
                ["ja-JP"] = new Texts
                {
                    Tab = "一般",
                    GenerationGroup = "生成",
                    IntegrationGroup = "ゲームとの連携",
                    MaintenanceGroup = "メンテナンス",
                    AutoLabel = "アイコンを自動生成",
                    AutoDesc = "ゲームの起動後まもなく、アイコンがまだない建物や、前回の生成以降に変更された建物のアイコンを生成します。フォルダから削除されたアイコンは再生成されます。処理は複数のフレームに分散して行われるため、メニューの操作が重くなることはありません。",
                    ReplaceLabel = "生成したアイコンをゲーム内で使用",
                    ReplaceDesc = "標準のサムネイルの代わりに、生成したアイコンをゲームのインターフェースに表示します。区画の建物と特殊施設のみ、またはすべての建物（サービス、交通、公園、ランドマーク、アップグレードを含む）から選べます。「オフ」にすると、元のアイコンがすぐに復元されます。ゲームのファイルは変更されません。",
                    NowLabel = "不足しているアイコンを今すぐ生成",
                    NowDesc = "次回の起動を待たずに、不足しているアイコンや古くなったアイコンを今すぐ生成します。",
                    AllLabel = "すべてのアイコンを再生成",
                    AllDesc = "最新のものも含め、すべてのアイコンを再生成します。",
                    AllWarning = "すべての建物アイコンを再生成します。数分かかる場合があります。続けますか？",
                    FolderLabel = "アイコンフォルダを開く",
                    FolderDesc = "建物ごとに1つのPNGファイルが保存されている ModsData/BuildingIconGenerator/icons フォルダを開きます。",
                    ProgressTitle = "建物アイコンを生成中",
                    ProgressText = "{DONE} / {TOTAL}",
                    FinishedTitle = "建物アイコンの生成完了",
                    FinishedText = "{COUNT}個のアイコンを生成しました（{SECONDS}秒）",
                    IconsOff = "オフ",
                    IconsZoned = "区画の建物と特殊施設",
                    IconsAll = "すべての建物",
                    CleanupLabel = "使われていないアイコンを削除",
                    CleanupDesc = "現在インストールされていない建物（削除したModの建物など）のアイコンと、どの建物にも属さないアイコンフォルダ内のPNGファイルを削除します。そのコンテンツを再びインストールすると、アイコンは自動的に再生成されます。",
                    CleanupWarning = "現在読み込まれていないすべての建物のアイコンを削除します。続けますか？",
                    CleanupTitle = "使われていないアイコンを削除しました",
                    CleanupText = "削除数：{COUNT}",
                },
                ["ko-KR"] = new Texts
                {
                    Tab = "일반",
                    GenerationGroup = "생성",
                    IntegrationGroup = "게임 연동",
                    MaintenanceGroup = "유지 관리",
                    AutoLabel = "아이콘 자동 생성",
                    AutoDesc = "게임이 시작되고 잠시 후, 아직 아이콘이 없거나 마지막 생성 이후 변경된 건물의 아이콘을 생성합니다. 폴더에서 삭제된 아이콘은 다시 생성됩니다. 작업을 여러 프레임에 나누어 처리하므로 메뉴를 계속 원활하게 사용할 수 있습니다.",
                    ReplaceLabel = "생성된 아이콘을 게임에서 사용",
                    ReplaceDesc = "기본 섬네일 대신 생성된 아이콘을 게임 인터페이스에 표시합니다. 구역 건물과 고유 건물만, 또는 모든 건물(서비스, 교통, 공원, 랜드마크, 업그레이드 포함)을 선택할 수 있습니다. '끔'으로 설정하면 원래 아이콘이 즉시 복원됩니다. 게임 파일은 수정되지 않습니다.",
                    NowLabel = "누락된 아이콘 지금 생성",
                    NowDesc = "다음 실행을 기다리지 않고 누락되었거나 오래된 아이콘을 지금 생성합니다.",
                    AllLabel = "모든 아이콘 다시 생성",
                    AllDesc = "최신 아이콘을 포함해 모든 아이콘을 다시 생성합니다.",
                    AllWarning = "모든 건물 아이콘을 다시 생성합니다. 몇 분 정도 걸릴 수 있습니다. 계속하시겠습니까?",
                    FolderLabel = "아이콘 폴더 열기",
                    FolderDesc = "건물마다 PNG 파일이 하나씩 저장되는 ModsData/BuildingIconGenerator/icons 폴더를 엽니다.",
                    ProgressTitle = "건물 아이콘 생성 중",
                    ProgressText = "{DONE} / {TOTAL}",
                    FinishedTitle = "건물 아이콘 준비 완료",
                    FinishedText = "아이콘 {COUNT}개 생성 완료 ({SECONDS}초)",
                    IconsOff = "끔",
                    IconsZoned = "구역 건물 및 고유 건물",
                    IconsAll = "모든 건물",
                    CleanupLabel = "사용하지 않는 아이콘 삭제",
                    CleanupDesc = "현재 설치되어 있지 않은 건물(제거한 모드의 건물 등)의 아이콘과, 어떤 건물에도 속하지 않는 아이콘 폴더의 PNG 파일을 삭제합니다. 해당 콘텐츠를 다시 설치하면 아이콘이 자동으로 다시 생성됩니다.",
                    CleanupWarning = "현재 불러오지 않은 모든 건물의 아이콘이 삭제됩니다. 계속하시겠습니까?",
                    CleanupTitle = "사용하지 않는 아이콘 삭제 완료",
                    CleanupText = "삭제됨: {COUNT}",
                },
                ["zh-HANS"] = new Texts
                {
                    Tab = "综合",
                    GenerationGroup = "生成",
                    IntegrationGroup = "游戏集成",
                    MaintenanceGroup = "维护",
                    AutoLabel = "自动生成图标",
                    AutoDesc = "游戏启动后不久，为每个尚无图标或自上次生成后有所变化的建筑生成图标。从文件夹中删除的图标会重新生成。生成工作分摊到多帧完成，菜单仍可流畅操作。",
                    ReplaceLabel = "在游戏中使用生成的图标",
                    ReplaceDesc = "在游戏界面中以生成的图标替代默认缩略图：可仅用于功能区建筑和标志性建筑，或用于所有建筑（包括服务、交通运输、公园、地标和升级）。选择“关”会立即恢复原始图标。不会修改任何游戏文件。",
                    NowLabel = "立即生成缺失的图标",
                    NowDesc = "无需等待下次启动，立即生成缺失或过时的图标。",
                    AllLabel = "重新生成所有图标",
                    AllDesc = "重新生成所有图标，包括已是最新的图标。",
                    AllWarning = "将重新生成所有建筑图标，可能需要几分钟。是否继续？",
                    FolderLabel = "打开图标文件夹",
                    FolderDesc = "打开 ModsData/BuildingIconGenerator/icons 文件夹，其中为每个建筑保存一个 PNG 文件。",
                    ProgressTitle = "正在生成建筑图标",
                    ProgressText = "{DONE} / {TOTAL}",
                    FinishedTitle = "建筑图标已就绪",
                    FinishedText = "已生成{COUNT}个图标，用时{SECONDS}秒",
                    IconsOff = "关",
                    IconsZoned = "功能区建筑和标志性建筑",
                    IconsAll = "所有建筑",
                    CleanupLabel = "删除未使用的图标",
                    CleanupDesc = "删除当前未安装的建筑（例如已移除模组中的建筑）的图标，以及图标文件夹中不属于任何建筑的其他 PNG 文件。重新安装这些内容后，其图标会自动重新生成。",
                    CleanupWarning = "将删除当前未加载的所有建筑的图标。是否继续？",
                    CleanupTitle = "已删除未使用的图标",
                    CleanupText = "已删除：{COUNT}",
                },
                ["zh-HANT"] = new Texts
                {
                    Tab = "綜合",
                    GenerationGroup = "生成",
                    IntegrationGroup = "遊戲整合",
                    MaintenanceGroup = "維護",
                    AutoLabel = "自動生成圖示",
                    AutoDesc = "遊戲啟動後不久，會為每棟尚無圖示或自上次生成後有所變更的建築生成圖示。從資料夾中刪除的圖示會重新生成。生成工作會分散到多幀中進行，選單仍可流暢操作。",
                    ReplaceLabel = "在遊戲中使用生成的圖示",
                    ReplaceDesc = "在遊戲介面中以生成的圖示取代預設縮圖：可僅用於功能區建築和標誌性建築，或用於所有建築（包括服務、交通運輸、公園、地標和升級）。選擇「關閉」會立即還原原始圖示。不會修改任何遊戲檔案。",
                    NowLabel = "立即生成缺少的圖示",
                    NowDesc = "不必等到下次啟動，立即生成缺少或過時的圖示。",
                    AllLabel = "重新生成所有圖示",
                    AllDesc = "重新生成所有圖示，包括已是最新的圖示。",
                    AllWarning = "將重新生成所有建築圖示，可能需要幾分鐘。是否要繼續？",
                    FolderLabel = "開啟圖示資料夾",
                    FolderDesc = "開啟 ModsData/BuildingIconGenerator/icons 資料夾，其中為每棟建築儲存一個 PNG 檔案。",
                    ProgressTitle = "正在生成建築圖示",
                    ProgressText = "{DONE} / {TOTAL}",
                    FinishedTitle = "建築圖示已就緒",
                    FinishedText = "已生成 {COUNT} 個圖示，耗時 {SECONDS} 秒",
                    IconsOff = "關閉",
                    IconsZoned = "功能區建築和標誌性建築",
                    IconsAll = "所有建築",
                    CleanupLabel = "刪除未使用的圖示",
                    CleanupDesc = "刪除目前未安裝的建築（例如已移除模組中的建築）的圖示，以及圖示資料夾中不屬於任何建築的其他 PNG 檔案。重新安裝這些內容後，其圖示會自動重新生成。",
                    CleanupWarning = "將刪除目前未載入的所有建築的圖示。是否要繼續？",
                    CleanupTitle = "已刪除未使用的圖示",
                    CleanupText = "已刪除：{COUNT}",
                },
            };
        }

        /// <summary>The mod's strings in one language.</summary>
        private sealed class Texts
        {
            public string Tab;
            public string GenerationGroup;
            public string IntegrationGroup;
            public string MaintenanceGroup;
            public string AutoLabel;
            public string AutoDesc;
            public string ReplaceLabel;
            public string ReplaceDesc;
            public string NowLabel;
            public string NowDesc;
            public string AllLabel;
            public string AllDesc;
            public string AllWarning;
            public string FolderLabel;
            public string FolderDesc;
            public string ProgressTitle;
            public string ProgressText;
            public string FinishedTitle;
            public string FinishedText;
            public string IconsOff;
            public string IconsZoned;
            public string IconsAll;
            public string CleanupLabel;
            public string CleanupDesc;
            public string CleanupWarning;
            public string CleanupTitle;
            public string CleanupText;
        }

        private sealed class Source : IDictionarySource
        {
            private readonly Dictionary<string, string> m_Entries;

            public Source(Dictionary<string, string> entries)
            {
                m_Entries = entries;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return m_Entries;
            }

            public void Unload()
            {
            }
        }
    }
}
