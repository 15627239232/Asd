using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace QingfengShuAn
{
    /// <summary>清风书案 - 增强版写作MOD</summary>
    public class ModEntry : Mod
    {
        /********** 配置 **********/
        private ModConfig Config;
        private string BooksDirectory => Path.Combine(this.Helper.DirectoryPath, "books");
        private string ChaptersDirectory => Path.Combine(this.BooksDirectory, "chapters");
        
        /********** MOD入口 **********/
        public override void Entry(IModHelper helper)
        {
            this.Config = this.Helper.ReadConfig<ModConfig>() ?? new ModConfig();
            
            // 确保目录存在
            Directory.CreateDirectory(this.BooksDirectory);
            Directory.CreateDirectory(this.ChaptersDirectory);
            
            // 添加控制台命令
            helper.ConsoleCommands.Add("get_desk", "获取清风书案", (cmd, args) => this.GiveDeskCommand());
            helper.ConsoleCommands.Add("list_books", "列出所有书籍", (cmd, args) => this.ListBooksCommand());
            
            // 绑定事件
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            
            this.Monitor.Log("清风书案增强版已加载。", LogLevel.Info);
        }
        
        /********** 控制台命令 **********/
        private void GiveDeskCommand()
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("请先进入游戏世界。", LogLevel.Warn);
                return;
            }
            
            var desk = new StardewValley.Object("qingfeng_desk", 1)
            {
                name = "清风书案",
                displayName = "清风书案",
                description = "一张用于写作的书案。右键点击可开始写作。",
                price = 1000,
                category = StardewValley.Object.furnitureCategory,
                canBeSetDown = true
            };
            
            if (Game1.player.addItemToInventoryBool(desk))
            {
                this.Monitor.Log("已获得清风书案。", LogLevel.Info);
                Game1.showGlobalMessage("获得了清风书案！");
            }
        }
        
        private void ListBooksCommand()
        {
            var txtFiles = Directory.GetFiles(this.BooksDirectory, "*.txt");
            var jsonFiles = Directory.GetFiles(this.BooksDirectory, "*.json");
            
            this.Monitor.Log($"=== 找到 {txtFiles.Length + jsonFiles.Length} 本书 ===", LogLevel.Info);
            
            foreach (var file in txtFiles.Concat(jsonFiles).OrderByDescending(f => File.GetLastWriteTime(f)))
            {
                var info = new FileInfo(file);
                this.Monitor.Log($"{Path.GetFileName(file)} - {info.Length / 1024}KB - {info.LastWriteTime:yyyy-MM-dd}", LogLevel.Info);
            }
        }
        
        /********** 事件处理 **********/
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null)
                return;
                
            if (e.Button == this.Config.OpenKey)
            {
                this.OpenWritingMenu();
            }
        }
        
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // GMCM配置
            try
            {
                var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
                if (gmcm != null)
                {
                    gmcm.Register(this.ModManifest, 
                        reset: () => this.Config = new ModConfig(),
                        save: () => this.Helper.WriteConfig(this.Config));
                    
                    gmcm.AddKeybind(this.ModManifest, 
                        name: () => "打开写作界面", 
                        getValue: () => this.Config.OpenKey, 
                        setValue: value => this.Config.OpenKey = value);
                }
            }
            catch { }
        }
        
        /********** 核心方法 **********/
        private void OpenWritingMenu()
        {
            Game1.activeClickableMenu = new EnhancedWritingMenu(this.Helper, this.Monitor, 
                this.BooksDirectory, this.ChaptersDirectory);
        }
    }

    /********** 配置类 **********/
    public class ModConfig
    {
        public SButton OpenKey { get; set; } = SButton.F1;
        public bool AutoSave { get; set; } = true;
        public int AutoSaveInterval { get; set; } = 300; // 5分钟
        public int MaxCharactersPerChapter { get; set; } = 10000;
    }

    /********** 增强版写作菜单 **********/
    public class EnhancedWritingMenu : IClickableMenu
    {
        /********** 依赖 **********/
        private readonly IModHelper Helper;
        private readonly IMonitor Monitor;
        private readonly string BooksDirectory;
        private readonly string ChaptersDirectory;
        
        /********** UI元素 **********/
        private TextBox titleTextBox;
        private TextBox contentTextBox;
        private TextBox chapterTextBox;
        private readonly List<ClickableComponent> buttons = new List<ClickableComponent>();
        private readonly List<ClickableComponent> bookButtons = new List<ClickableComponent>();
        private readonly List<ClickableComponent> chapterButtons = new List<ClickableComponent>();
        private string notificationText = "";
        private int notificationTimer = 0;
        
        /********** 状态 **********/
        private enum ScreenMode { Writing, BookList, ChapterList, LoadBook }
        private ScreenMode currentScreen = ScreenMode.Writing;
        private List<BookInfo> allBooks = new List<BookInfo>();
        private List<ChapterInfo> bookChapters = new List<ChapterInfo>();
        
        /********** 当前书籍数据 **********/
        private string CurrentBookTitle = "";
        private string CurrentChapterTitle = "";
        private StringBuilder CurrentContent = new StringBuilder();
        private int totalWordCount = 0;
        private int totalChapterCount = 0;
        private List<string> chapterTitles = new List<string>();
        
        /********** 自动保存 **********/
        private int autoSaveTimer = 0;
        private const int AUTO_SAVE_INTERVAL = 300; // 5秒（游戏刻）
        
        /********** 构造函数 **********/
        public EnhancedWritingMenu(IModHelper helper, IMonitor monitor, string booksDir, string chaptersDir)
        {
            this.Helper = helper;
            this.Monitor = monitor;
            this.BooksDirectory = booksDir;
            this.ChaptersDirectory = chaptersDir;
            
            this.InitializeMenu();
            this.LoadBookList();
        }
        
        /********** 初始化 **********/
        private void InitializeMenu()
        {
            int width = 1200;
            int height = 800;
            int x = (Game1.viewport.Width - width) / 2;
            int y = (Game1.viewport.Height - height) / 2;
            
            this.initialize(x, y, width, height, true);
            
            // 书名输入框
            this.titleTextBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.dialogueFont, Game1.textColor)
            {
                X = x + 150, Y = y + 20, Width = 500, Height = 40,
                Text = this.CurrentBookTitle
            };
            this.titleTextBox.OnEnterPressed += (sender) => this.CurrentBookTitle = this.titleTextBox.Text;
            
            // 章节名输入框
            this.chapterTextBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = x + 150, Y = y + 70, Width = 300, Height = 30,
                Text = this.CurrentChapterTitle
            };
            this.chapterTextBox.OnEnterPressed += (sender) => this.CurrentChapterTitle = this.chapterTextBox.Text;
            
            // 内容输入框 - 使用更大的文本框
            this.contentTextBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = x + 20, Y = y + 120, Width = width - 250, Height = height - 200,
                Text = ""
            };
            
            // 左侧功能按钮
            int buttonY = y + 120;
            string[] leftButtons = { "新书", "保存", "保存章节", "章节列表", "书籍列表", "续写", "导入TXT", "导出TXT", "关闭" };
            
            for (int i = 0; i < leftButtons.Length; i++)
            {
                this.buttons.Add(new ClickableComponent(
                    new Rectangle(x + width - 220, buttonY + i * 55, 200, 50), 
                    leftButtons[i])
                {
                    myID = 100 + i,
                    name = leftButtons[i]
                });
            }
            
            this.populateClickableComponentList();
        }
        
        /********** 书籍管理 **********/
        private void LoadBookList()
        {
            this.allBooks.Clear();
            
            // 加载TXT书籍
            foreach (var file in Directory.GetFiles(this.BooksDirectory, "*.txt"))
            {
                try
                {
                    var info = new FileInfo(file);
                    var content = File.ReadAllText(file, Encoding.UTF8);
                    
                    this.allBooks.Add(new BookInfo
                    {
                        FileName = Path.GetFileName(file),
                        FullPath = file,
                        Title = Path.GetFileNameWithoutExtension(file).Split('_')[0],
                        WordCount = content.Length,
                        SizeKB = info.Length / 1024,
                        LastModified = info.LastWriteTime,
                        ChapterCount = this.CountChaptersForBook(Path.GetFileNameWithoutExtension(file))
                    });
                }
                catch { }
            }
            
            // 加载JSON书籍
            foreach (var file in Directory.GetFiles(this.BooksDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var bookData = Newtonsoft.Json.JsonConvert.DeserializeObject<BookData>(json);
                    var info = new FileInfo(file);
                    
                    this.allBooks.Add(new BookInfo
                    {
                        FileName = Path.GetFileName(file),
                        FullPath = file,
                        Title = bookData.Title,
                        WordCount = bookData.WordCount,
                        SizeKB = info.Length / 1024,
                        LastModified = info.LastWriteTime,
                        ChapterCount = bookData.ChapterTitles?.Count ?? 1
                    });
                }
                catch { }
            }
            
            this.allBooks = this.allBooks.OrderByDescending(b => b.LastModified).ToList();
        }
        
        private int CountChaptersForBook(string bookName)
        {
            try
            {
                string chapterDir = Path.Combine(this.ChaptersDirectory, bookName);
                if (Directory.Exists(chapterDir))
                {
                    return Directory.GetFiles(chapterDir, "*.txt").Length;
                }
            }
            catch { }
            return 1;
        }
        
        private void LoadBookChapters(string bookTitle)
        {
            this.bookChapters.Clear();
            
            // 加载主书文件
            string mainFile = Path.Combine(this.BooksDirectory, $"{bookTitle}.txt");
            if (File.Exists(mainFile))
            {
                this.bookChapters.Add(new ChapterInfo
                {
                    Title = "全书",
                    FilePath = mainFile,
                    IsMainFile = true
                });
            }
            
            // 加载章节文件
            string chapterDir = Path.Combine(this.ChaptersDirectory, bookTitle);
            if (Directory.Exists(chapterDir))
            {
                int chapterNum = 1;
                foreach (var file in Directory.GetFiles(chapterDir, "*.txt").OrderBy(f => f))
                {
                    string chapterTitle = Path.GetFileNameWithoutExtension(file);
                    this.bookChapters.Add(new ChapterInfo
                    {
                        Title = $"第{chapterNum}章: {chapterTitle}",
                        FilePath = file,
                        ChapterNumber = chapterNum++
                    });
                }
            }
        }
        
        /********** 核心功能 **********/
        private void NewBook()
        {
            this.CurrentBookTitle = "";
            this.CurrentChapterTitle = "";
            this.CurrentContent.Clear();
            this.titleTextBox.Text = "";
            this.chapterTextBox.Text = "";
            this.contentTextBox.Text = "";
            this.totalWordCount = 0;
            this.totalChapterCount = 0;
            this.chapterTitles.Clear();
            this.ShowNotification("新建书籍");
        }
        
        private void SaveBook()
        {
            if (string.IsNullOrWhiteSpace(this.CurrentBookTitle))
            {
                this.ShowNotification("请输入书名", true);
                return;
            }
            
            try
            {
                this.CurrentBookTitle = this.titleTextBox.Text.Trim();
                string content = this.contentTextBox.Text;
                
                // 保存为TXT
                string txtFile = Path.Combine(this.BooksDirectory, $"{this.CurrentBookTitle}.txt");
                File.WriteAllText(txtFile, content, Encoding.UTF8);
                
                // 保存为JSON（带元数据）
                var bookData = new EnhancedBookData
                {
                    Title = this.CurrentBookTitle,
                    Content = content,
                    ChapterTitles = this.chapterTitles,
                    TotalChapters = this.totalChapterCount,
                    TotalWords = content.Length,
                    Created = DateTime.Now,
                    LastModified = DateTime.Now,
                    GameDate = $"{Game1.currentSeason} {Game1.dayOfMonth}, 第{Game1.year}年"
                };
                
                string jsonFile = Path.Combine(this.BooksDirectory, $"{this.CurrentBookTitle}.json");
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(bookData, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(jsonFile, json, Encoding.UTF8);
                
                this.ShowNotification($"已保存: {this.CurrentBookTitle}");
                this.LoadBookList();
            }
            catch (Exception ex)
            {
                this.ShowNotification($"保存失败: {ex.Message}", true);
            }
        }
        
        private void SaveChapter()
        {
            if (string.IsNullOrWhiteSpace(this.CurrentBookTitle))
            {
                this.ShowNotification("请先输入书名", true);
                return;
            }
            
            if (string.IsNullOrWhiteSpace(this.CurrentChapterTitle))
            {
                this.CurrentChapterTitle = $"第{this.totalChapterCount + 1}章";
            }
            
            try
            {
                // 创建书籍目录
                string bookChapterDir = Path.Combine(this.ChaptersDirectory, this.CurrentBookTitle);
                Directory.CreateDirectory(bookChapterDir);
                
                // 保存章节
                string chapterFile = Path.Combine(bookChapterDir, $"{this.CurrentChapterTitle}.txt");
                File.WriteAllText(chapterFile, this.contentTextBox.Text, Encoding.UTF8);
                
                this.chapterTitles.Add(this.CurrentChapterTitle);
                this.totalChapterCount++;
                
                // 清空当前内容，准备下一章
                this.CurrentChapterTitle = "";
                this.chapterTextBox.Text = "";
                this.contentTextBox.Text = "";
                
                this.ShowNotification($"已保存章节: {this.CurrentChapterTitle}");
            }
            catch (Exception ex)
            {
                this.ShowNotification($"保存章节失败: {ex.Message}", true);
            }
        }
        
        private void LoadBookForContinue(string bookTitle)
        {
            try
            {
                // 尝试加载TXT
                string txtFile = Path.Combine(this.BooksDirectory, $"{bookTitle}.txt");
                if (File.Exists(txtFile))
                {
                    string content = File.ReadAllText(txtFile, Encoding.UTF8);
                    this.CurrentBookTitle = bookTitle;
                    this.titleTextBox.Text = bookTitle;
                    this.contentTextBox.Text = content;
                    this.CurrentContent = new StringBuilder(content);
                    this.totalWordCount = content.Length;
                    
                    this.ShowNotification($"已加载书籍: {bookTitle}");
                    this.currentScreen = ScreenMode.Writing;
                    return;
                }
                
                // 尝试加载JSON
                string jsonFile = Path.Combine(this.BooksDirectory, $"{bookTitle}.json");
                if (File.Exists(jsonFile))
                {
                    string json = File.ReadAllText(jsonFile, Encoding.UTF8);
                    var bookData = Newtonsoft.Json.JsonConvert.DeserializeObject<EnhancedBookData>(json);
                    
                    this.CurrentBookTitle = bookData.Title;
                    this.titleTextBox.Text = bookData.Title;
                    this.contentTextBox.Text = bookData.Content;
                    this.CurrentContent = new StringBuilder(bookData.Content);
                    this.totalWordCount = bookData.TotalWords;
                    this.totalChapterCount = bookData.TotalChapters;
                    this.chapterTitles = bookData.ChapterTitles ?? new List<string>();
                    
                    this.ShowNotification($"已加载书籍: {bookTitle}");
                    this.currentScreen = ScreenMode.Writing;
                }
            }
            catch (Exception ex)
            {
                this.ShowNotification($"加载失败: {ex.Message}", true);
            }
        }
        
        private void AppendToBook(string content)
        {
            this.CurrentContent.AppendLine();
            this.CurrentContent.AppendLine();
            this.CurrentContent.AppendLine($"--- 续写于 {DateTime.Now:yyyy-MM-dd HH:mm} ---");
            this.CurrentContent.AppendLine();
            this.CurrentContent.Append(content);
            this.contentTextBox.Text = this.CurrentContent.ToString();
            this.totalWordCount = this.CurrentContent.Length;
        }
        
        /********** 界面切换 **********/
        private void ShowBookList()
        {
            this.currentScreen = ScreenMode.BookList;
            this.LoadBookList();
            
            // 创建书籍列表按钮
            this.bookButtons.Clear();
            int startY = this.yPositionOnScreen + 50;
            
            for (int i = 0; i < Math.Min(15, this.allBooks.Count); i++)
            {
                var book = this.allBooks[i];
                string displayText = $"{book.Title} ({book.WordCount}字, {book.ChapterCount}章)";
                
                this.bookButtons.Add(new ClickableComponent(
                    new Rectangle(this.xPositionOnScreen + 50, startY + i * 40, 800, 35),
                    displayText)
                {
                    myID = 200 + i,
                    name = book.Title
                });
            }
        }
        
        private void ShowChapterList()
        {
            if (string.IsNullOrWhiteSpace(this.CurrentBookTitle))
            {
                this.ShowNotification("请先选择一本书", true);
                return;
            }
            
            this.currentScreen = ScreenMode.ChapterList;
            this.LoadBookChapters(this.CurrentBookTitle);
            
            this.chapterButtons.Clear();
            int startY = this.yPositionOnScreen + 50;
            
            for (int i = 0; i < this.bookChapters.Count; i++)
            {
                var chapter = this.bookChapters[i];
                this.chapterButtons.Add(new ClickableComponent(
                    new Rectangle(this.xPositionOnScreen + 50, startY + i * 40, 800, 35),
                    chapter.Title)
                {
                    myID = 300 + i,
                    name = chapter.FilePath
                });
            }
        }
        
        /********** 自动保存 **********/
        private void CheckAutoSave()
        {
            this.autoSaveTimer++;
            if (this.autoSaveTimer >= AUTO_SAVE_INTERVAL && !string.IsNullOrWhiteSpace(this.CurrentBookTitle))
            {
                this.autoSaveTimer = 0;
                
                // 保存草稿
                string draftFile = Path.Combine(this.BooksDirectory, $"{this.CurrentBookTitle}_draft.txt");
                File.WriteAllText(draftFile, this.contentTextBox.Text, Encoding.UTF8);
                
                this.ShowNotification("已自动保存草稿");
            }
        }
        
        /********** 渲染 **********/
        public override void draw(SpriteBatch b)
        {
            // 半透明背景
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            
            // 主面板
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), 
                this.xPositionOnScreen, this.yPositionOnScreen,
                this.width, this.height, Color.White, 4f);
            
            switch (this.currentScreen)
            {
                case ScreenMode.Writing:
                    this.DrawWritingScreen(b);
                    break;
                    
                case ScreenMode.BookList:
                    this.DrawBookListScreen(b);
                    break;
                    
                case ScreenMode.ChapterList:
                    this.DrawChapterListScreen(b);
                    break;
            }
            
            // 通知
            this.DrawNotification(b);
            
            // 鼠标
            this.drawMouse(b);
        }
        
        private void DrawWritingScreen(SpriteBatch b)
        {
            // 标题
            Utility.drawTextWithShadow(b, "清风书案 - 增强版", Game1.dialogueFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen - 50),
                Color.Gold, 1f);
            
            // 标签
            Utility.drawTextWithShadow(b, "书名:", Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen + 25),
                Color.White);
            this.titleTextBox.Draw(b);
            
            Utility.drawTextWithShadow(b, "章节:", Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen + 75),
                Color.White);
            this.chapterTextBox.Draw(b);
            
            // 内容区域
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), 
                this.xPositionOnScreen + 15, this.yPositionOnScreen + 115,
                this.width - 240, this.height - 140, Color.White, 4f);
            this.contentTextBox.Draw(b);
            
            // 统计信息
            string stats = $"总字数: {this.totalWordCount}  |  总章节: {this.totalChapterCount}  |  当前字数: {this.contentTextBox.Text.Length}";
            Utility.drawTextWithShadow(b, stats, Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen + this.height - 80),
                Color.LightGray);
            
            // 功能按钮
            foreach (var button in this.buttons)
            {
                bool isHover = button.bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
                Color color = isHover ? new Color(106, 138, 174) : new Color(86, 108, 134);
                
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), 
                    button.bounds.X, button.bounds.Y,
                    button.bounds.Width, button.bounds.Height, color, 4f, false);
                
                Vector2 textSize = Game1.smallFont.MeasureString(button.name);
                Vector2 textPos = new Vector2(
                    button.bounds.X + (button.bounds.Width - textSize.X) / 2,
                    button.bounds.Y + (button.bounds.Height - textSize.Y) / 2);
                
                Utility.drawTextWithShadow(b, button.name, Game1.smallFont, textPos, Color.White);
            }
            
            // 当前书籍提示
            if (!string.IsNullOrEmpty(this.CurrentBookTitle))
            {
                string bookInfo = $"当前: {this.CurrentBookTitle}";
                Utility.drawTextWithShadow(b, bookInfo, Game1.smallFont,
                    new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen + this.height - 40),
                    Color.LightGreen);
            }
        }
        
        private void DrawBookListScreen(SpriteBatch b)
        {
            Utility.drawTextWithShadow(b, "书籍列表 (点击加载)", Game1.dialogueFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen - 50),
                Color.Gold, 1f);
            
            Utility.drawTextWithShadow(b, $"共找到 {this.allBooks.Count} 本书", Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen - 10),
                Color.White);
            
            for (int i = 0; i < this.bookButtons.Count; i++)
            {
                var button = this.bookButtons[i];
                bool isHover = button.bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
                Color bgColor = isHover ? new Color(86, 108, 134, 200) : new Color(60, 60, 60, 200);
                
                b.Draw(Game1.fadeToBlackRect, button.bounds, bgColor);
                
                // 绘制书籍信息
                if (i < this.allBooks.Count)
                {
                    var book = this.allBooks[i];
                    string info = $"{i + 1}. {button.name}";
                    Utility.drawTextWithShadow(b, info, Game1.smallFont,
                        new Vector2(button.bounds.X + 10, button.bounds.Y + 5),
                        Color.White);
                    
                    string details = $"{book.WordCount}字 | {book.ChapterCount}章 | {book.LastModified:yyyy-MM-dd}";
                    Utility.drawTextWithShadow(b, details, Game1.tinyFont,
                        new Vector2(button.bounds.X + 10, button.bounds.Y + 25),
                        Color.LightGray);
                }
            }
            
            // 返回按钮
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), 
                this.xPositionOnScreen + this.width - 220, this.yPositionOnScreen + 20,
                200, 50, Color.White, 4f, false);
            Utility.drawTextWithShadow(b, "返回写作", Game1.smallFont,
                new Vector2(this.xPositionOnScreen + this.width - 220 + 50, this.yPositionOnScreen + 35),
                Color.White);
        }
        
        private void DrawChapterListScreen(SpriteBatch b)
        {
            Utility.drawTextWithShadow(b, $"《{this.CurrentBookTitle}》章节列表", Game1.dialogueFont,
                new Vector2(this.xPositionOnScreen + 20, this.yPositionOnScreen - 50),
                Color.Gold, 1f);
            
            for (int i = 0; i < this.chapterButtons.Count; i++)
            {
                var button = this.chapterButtons[i];
                bool isHover = button.bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
                Color bgColor = isHover ? new Color(86, 108, 134, 200) : new Color(60, 60, 60, 200);
                
                b.Draw(Game1.fadeToBlackRect, button.bounds, bgColor);
                Utility.drawTextWithShadow(b, button.name, Game1.smallFont,
                    new Vector2(button.bounds.X + 10, button.bounds.Y + 10),
                    Color.White);
            }
            
            // 返回按钮
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), 
                this.xPositionOnScreen + this.width - 220, this.yPositionOnScreen + 20,
                200, 50, Color.White, 4f, false);
            Utility.drawTextWithShadow(b, "返回写作", Game1.smallFont,
                new Vector2(this.xPositionOnScreen + this.width - 220 + 50, this.yPositionOnScreen + 35),
                Color.White);
        }
        
        private void DrawNotification(SpriteBatch b)
        {
            if (this.notificationTimer > 0)
            {
                Vector2 notifSize = Game1.smallFont.MeasureString(this.notificationText);
                Vector2 notifPos = new Vector2(
                    (Game1.viewport.Width - notifSize.X) / 2,
                    this.yPositionOnScreen + 10);
                
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), 
                    (int)notifPos.X - 10, (int)notifPos.Y - 5,
                    (int)notifSize.X + 20, (int)notifSize.Y + 10, Color.Black * 0.8f, 2f);
                Utility.drawTextWithShadow(b, this.notificationText, Game1.smallFont, notifPos, Color.White);
                this.notificationTimer--;
            }
        }
        
        /********** 输入处理 **********/
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);
            
            this.titleTextBox.Update();
            this.chapterTextBox.Update();
            this.contentTextBox.Update();
            
            switch (this.currentScreen)
            {
                case ScreenMode.Writing:
                    this.HandleWritingScreenClick(x, y, playSound);
                    break;
                    
                case ScreenMode.BookList:
                    this.HandleBookListClick(x, y, playSound);
                    break;
                    
                case ScreenMode.ChapterList:
                    this.HandleChapterListClick(x, y, playSound);
                    break;
            }
        }
        
        private void HandleWritingScreenClick(int x, int y, bool playSound)
        {
            foreach (var button in this.buttons)
            {
                if (button.bounds.Contains(x, y))
                {
                    if (playSound) Game1.playSound("coin");
                    
                    switch (button.name)
                    {
                        case "新书": this.NewBook(); break;
                        case "保存": this.SaveBook(); break;
                        case "保存章节": this.SaveChapter(); break;
                        case "章节列表": this.ShowChapterList(); break;
                        case "书籍列表": this.ShowBookList(); break;
                        case "续写": this.ShowBookList(); this.currentScreen = ScreenMode.LoadBook; break;
                        case "导入TXT": this.ShowNotification("导入功能开发中"); break;
                        case "导出TXT": this.ShowNotification("导出功能开发中"); break;
                        case "关闭": Game1.exitActiveMenu(); break;
                    }
                    return;
                }
            }
        }
        
        private void HandleBookListClick(int x, int y, bool playSound)
        {
            // 检查书籍按钮
            for (int i = 0; i < this.bookButtons.Count; i++)
            {
                if (this.bookButtons[i].bounds.Contains(x, y) && i < this.allBooks.Count)
                {
                    if (playSound) Game1.playSound("coin");
                    
                    if (this.currentScreen == ScreenMode.LoadBook)
                    {
                        // 续写模式：追加内容
                        this.LoadBookForContinue(this.allBooks[i].Title);
                        this.AppendToBook(this.contentTextBox.Text);
                    }
                    else
                    {
                        // 正常加载
                        this.LoadBookForContinue(this.allBooks[i].Title);
                    }
                    return;
                }
            }
            
            // 返回按钮
            if (x >= this.xPositionOnScreen + this.width - 220 && x <= this.xPositionOnScreen + this.width - 20 &&
                y >= this.yPositionOnScreen + 20 && y <= this.yPositionOnScreen + 70)
            {
                if (playSound) Game1.playSound("coin");
                this.currentScreen = ScreenMode.Writing;
            }
        }
        
        private void HandleChapterListClick(int x, int y, bool playSound)
        {
            // 检查章节按钮
            for (int i = 0; i < this.chapterButtons.Count; i++)
            {
                if (this.chapterButtons[i].bounds.Contains(x, y))
                {
                    if (playSound) Game1.playSound("coin");
                    
                    // 加载章节内容
                    try
                    {
                        string content = File.ReadAllText(this.chapterButtons[i].name, Encoding.UTF8);
                        this.contentTextBox.Text = content;
                        this.ShowNotification($"已加载章节");
                    }
                    catch { }
                    return;
                }
            }
            
            // 返回按钮
            if (x >= this.xPositionOnScreen + this.width - 220 && x <= this.xPositionOnScreen + this.width - 20 &&
                y >= this.yPositionOnScreen + 20 && y <= this.yPositionOnScreen + 70)
            {
                if (playSound) Game1.playSound("coin");
                this.currentScreen = ScreenMode.Writing;
            }
        }
        
        /********** 更新 **********/
        public override void update(GameTime time)
        {
            base.update(time);
            this.titleTextBox.Update();
            this.chapterTextBox.Update();
            this.contentTextBox.Update();
            this.CheckAutoSave();
        }
        
        /********** 辅助方法 **********/
        private void ShowNotification(string text, bool isError = false)
        {
            this.notificationText = text;
            this.notificationTimer = 120;
        }
    }

    /********** 数据结构 **********/
    public class BookInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Title { get; set; } = "";
        public int WordCount { get; set; }
        public long SizeKB { get; set; }
        public DateTime LastModified { get; set; }
        public int ChapterCount { get; set; }
    }
    
    public class ChapterInfo
    {
        public string Title { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int ChapterNumber { get; set; }
        public bool IsMainFile { get; set; }
    }
    
    public class EnhancedBookData
    {
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public List<string> ChapterTitles { get; set; } = new List<string>();
        public int TotalChapters { get; set; }
        public int TotalWords { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public string GameDate { get; set; } = "";
    }
}

