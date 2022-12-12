﻿using BarRaider.SdTools;
using ChatPager.Backend;
using ChatPager.Twitch;
using ChatPager.Wrappers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Remoting;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatPager
{

    //---------------------------------------------------
    //          BarRaider's Hall Of Fame
    // fex2stroke - Tip: 21.76
    //---------------------------------------------------

    [PluginActionId("com.barraider.alertflasher")]
    class AlertFlasher : KeypadBase
    {
        private enum FlashMode
        {
            Pager,
            ActiveStreamers,
            ChatMessage
        }


        #region Private Members
        private const int NUMBER_OF_SPECIAL_KEYS = 3; // Exit, Prev, Next

        private const int LONG_KEYPRESS_LENGTH_MS = 600;
        private const string RAID_COMMAND = "/raid ";
        private const string HOST_COMMAND = "/host ";

        private int stringMessageIndex;
        private readonly int deviceColumns = 0;
        private readonly int locationRow = 0;
        private readonly int locationColumn = 0;
        private readonly int sequentialKey;
        private int pagedSequentialKey = 0;
        private bool twoLettersPerKey;
        private string channelName;
        private TwitchLiveStreamersLongPressAction liveStreamersLongPressAction;
        private string chatMessage;
        private FlashMode flashMode;
        private int numberOfElements = 0;
        private int numberOfKeys = 0;
        private bool keyPressed = false;
        private bool longKeyPressed = false;
        private DateTime keyPressStart;
        private Image currentDrawnImage = null;


        #endregion

        public AlertFlasher(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            Logger.Instance.LogMessage(TracingLevel.DEBUG, $"[{Thread.CurrentThread.ManagedThreadId}] AlertFlasher loading");
            var deviceInfo = payload.DeviceInfo.Devices.Where(d => d.Id == connection.DeviceId).FirstOrDefault();

            stringMessageIndex = -1;
            sequentialKey = 0;
            if (deviceInfo != null && payload?.Coordinates != null)
            {
                deviceColumns = deviceInfo.Size.Cols;
                locationRow = payload.Coordinates.Row;
                locationColumn = payload.Coordinates.Column;
                sequentialKey = (deviceColumns * locationRow) + locationColumn;
            }
            Connection.GetGlobalSettingsAsync();
            AlertManager.Instance.FlashStatusChanged += Instance_FlashStatusChanged;
            AlertManager.Instance.ActiveStreamersChanged += Instance_ActiveStreamersChanged;
            AlertManager.Instance.ChatMessageListChanged += Instance_ChatMessageListChanged;
            Logger.Instance.LogMessage(TracingLevel.DEBUG, $"[{Thread.CurrentThread.ManagedThreadId}] AlertFlasher up: {sequentialKey}");
        }

        public override void Dispose()
        {
            //Logger.Instance.LogMessage(TracingLevel.DEBUG, $"[{Thread.CurrentThread.ManagedThreadId}] AlertFlasher going down: {sequentialKey}");
            AlertManager.Instance.FlashStatusChanged -= Instance_FlashStatusChanged;
            AlertManager.Instance.ActiveStreamersChanged -= Instance_ActiveStreamersChanged;
            AlertManager.Instance.ChatMessageListChanged -= Instance_ChatMessageListChanged;
        }

        public override void KeyPressed(KeyPayload payload)
        {
            keyPressed = true;
            longKeyPressed = false;
            keyPressStart = DateTime.Now;
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Keypressed {this.GetType()}");
        }

        public override void KeyReleased(KeyPayload payload)
        {
            keyPressed = false;
            if (longKeyPressed) // Take care of the short keypress
            {
                return;
            }

            // Handle a Short Keypress
            Logger.Instance.LogMessage(TracingLevel.INFO, $"Short Keypress {this.GetType()}");

            // Exit the full screen if Exit key or Pager is pressed
            if (flashMode == FlashMode.Pager || sequentialKey == 0)
            {
                AlertManager.Instance.StopFlashAndReset();
                Connection.SwitchProfileAsync(null);
            }
            else if (flashMode == FlashMode.ChatMessage)
            {
                if (sequentialKey == numberOfKeys - 1 && numberOfElements + NUMBER_OF_SPECIAL_KEYS >= pagedSequentialKey) // Next key is pressed
                {
                    // Move to next page
                    AlertManager.Instance.MoveToNextChatPage();
                }
                else if (sequentialKey == numberOfKeys - 2 && sequentialKey < pagedSequentialKey) // Prev Key is pressed
                {
                    AlertManager.Instance.MoveToPrevChatPage();
                }
                else if (!String.IsNullOrEmpty(chatMessage))
                {
                    if (!String.IsNullOrEmpty(channelName))
                    {
                        TwitchChat.Instance.SendMessage(channelName, chatMessage);
                        ConfirmCurrentImage();
                    }
                    else
                    {
                        TwitchChat.Instance.SendMessage(chatMessage);
                        ConfirmCurrentImage();
                    }

                }
            }
            else if (flashMode == FlashMode.ActiveStreamers)
            {
                if (sequentialKey == numberOfKeys - 1 && numberOfElements + NUMBER_OF_SPECIAL_KEYS >= pagedSequentialKey) // Next key is pressed
                {
                    // Move to next page
                    AlertManager.Instance.MoveToNextStreamersPage();
                }
                else if (sequentialKey == numberOfKeys - 2 && sequentialKey < pagedSequentialKey) // Prev Key is pressed
                {
                    AlertManager.Instance.MoveToPrevStreamersPage();
                }
                else if (!String.IsNullOrEmpty(channelName)) // Normal key
                {
                    System.Diagnostics.Process.Start(String.Format("https://twitch.tv/{0}", channelName));
                    ConfirmCurrentImage();
                }
            }
        }

        public override void OnTick()
        {
            if (keyPressed && !longKeyPressed)
            {
                int timeKeyWasPressed = (int)(DateTime.Now - keyPressStart).TotalMilliseconds;
                if (timeKeyWasPressed >= LONG_KEYPRESS_LENGTH_MS)
                {
                    HandleLongKeyPress();
                }
            }
        }

        public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
        {
            if (payload?.Settings != null)
            {
                TwitchGlobalSettings global = payload.Settings.ToObject<TwitchGlobalSettings>();
                twoLettersPerKey = global.TwoLettersPerKey;
                CalculateStringIndex();
            }
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {

        }

        private void CalculateStringIndex()
        {
            int multiplicationFactor = twoLettersPerKey ? 2 : 1;
            stringMessageIndex = multiplicationFactor * sequentialKey;
        }

        private void Instance_FlashStatusChanged(object sender, FlashStatusEventArgs e)
        {
            flashMode = FlashMode.Pager;
            _ = FlashImage(e.FlashMessage, e.FlashColor);
        }

        private async void Instance_ActiveStreamersChanged(object sender, TwitchLiveStreamersEventArgs e)
        {
            flashMode = FlashMode.ActiveStreamers;
            pagedSequentialKey = e.CurrentPage * (e.NumberOfKeys - NUMBER_OF_SPECIAL_KEYS) + sequentialKey; // -3 for the Exit, Back, Next buttons
            channelName = String.Empty;
            liveStreamersLongPressAction = e.LongPressAction;
            
            if (await HandleActiveStreamersNavigationKeys(e))
            {
                return;
            }

            if (e.DisplaySettings != null && e.DisplaySettings.Streamers != null && e.DisplaySettings.Streamers.Length >= pagedSequentialKey) // >= because we're doing -1 as we're starting on the second key
            {
                await Connection.SetTitleAsync(null);
                var streamerInfo = e.DisplaySettings.Streamers[pagedSequentialKey - 1];

                switch (e.DisplaySettings.DisplayImage)
                {
                    case ChannelDisplayImage.StreamPreview:
                        using (Image image = await HelperFunctions.FetchImage(HelperFunctions.GenerateUrlFromGenericImageUrl(streamerInfo.ThumbnailURL)))
                        {
                            await DrawStreamerImage(streamerInfo, image);
                        }
                        break;
                    case ChannelDisplayImage.GameIcon:
                        var gameInfo = await TwitchChannelInfoManager.Instance.GetGameInfo(streamerInfo.GameId);
                        if (gameInfo != null)
                        {
                            if (gameInfo.GameImage == null)
                            {
                                Logger.Instance.LogMessage(TracingLevel.WARN, $"{this.GetType()} ActiveStreamers - Game Image is null for {streamerInfo.UserDisplayName} {streamerInfo.GameName}");
                            }
                            using (Image gameImage = (Image) gameInfo.GameImage.Clone())
                            {
                                await DrawStreamerImage(streamerInfo, gameImage);
                            }
                        }
                        else
                        {
                            Logger.Instance.LogMessage(TracingLevel.WARN, $"{this.GetType()} ActiveStreamers - Game Info is empty for {streamerInfo.UserDisplayName} {streamerInfo.GameName}");
                        }
                        break;
                    case ChannelDisplayImage.UserIcon:
                        var userInfo = await TwitchUserInfoManager.Instance.GetUserInfo(streamerInfo.UserName);
                        if (userInfo != null)
                        {
                            using (Image thumbnailImage = await HelperFunctions.FetchImage(HelperFunctions.GenerateUrlFromGenericImageUrl(userInfo.ProfileImageUrl)))
                            {
                                await DrawStreamerImage(streamerInfo, thumbnailImage);
                            }
                        }
                        else
                        {
                            Logger.Instance.LogMessage(TracingLevel.WARN, $"{this.GetType()} ActiveStreamers - User Info is empty for {streamerInfo.UserName}");
                        }
                        break;
                }
                channelName = streamerInfo?.UserName;
            }
        }

        private async Task<bool> HandleActiveStreamersNavigationKeys(TwitchLiveStreamersEventArgs e)
        {
            if (sequentialKey == 0)
            {
                await Connection.SetTitleAsync("Exit");
                return true;
            }
            
            if (e.DisplaySettings != null && e.DisplaySettings.Streamers != null && sequentialKey == e.NumberOfKeys - 1 && e.DisplaySettings.Streamers.Length > e.NumberOfKeys - 3) // Last (Next) key, and there is more than one page *overall*
            {
                if (e.DisplaySettings.Streamers.Length + NUMBER_OF_SPECIAL_KEYS < pagedSequentialKey) // We are on last page
                {
                    await Connection.SetTitleAsync(null);
                }
                else
                {
                    await Connection.SetTitleAsync(">>");
                }
                numberOfElements = e.DisplaySettings.Streamers.Length;
                numberOfKeys = e.NumberOfKeys;
                return true;
            }
            
            if (e.DisplaySettings != null && e.DisplaySettings.Streamers != null && sequentialKey == e.NumberOfKeys - 2 && e.DisplaySettings.Streamers.Length > e.NumberOfKeys - 3) // Prev key, and there is more than one page *overall*
            {
                if (sequentialKey == pagedSequentialKey) // We are on the first page
                {
                    await Connection.SetTitleAsync(null);
                }
                else
                {
                    await Connection.SetTitleAsync("<<");
                }
                numberOfElements = e.DisplaySettings.Streamers.Length;
                numberOfKeys = e.NumberOfKeys;
                return true;
            }

            return false;
        }

        private async void Instance_ChatMessageListChanged(object sender, ChatMessageListEventArgs e)
        {
            flashMode = FlashMode.ChatMessage;
            channelName = e.Channel;
            pagedSequentialKey = e.CurrentPage * (e.NumberOfKeys - NUMBER_OF_SPECIAL_KEYS) + sequentialKey; // -3 for the Exit, Back, Next buttons

            if (await HandleChatMessageListNavigationKeys(e))
            {
                return;
            }
           
            if (e.ChatMessageKeys != null && e.ChatMessageKeys.Length >= pagedSequentialKey) // >= because we're doing -1 as we're starting on the second key
            // +1 because starting on second key
            {
                await Connection.SetTitleAsync(null);
                var userInfo = e.ChatMessageKeys[pagedSequentialKey - 1];
                string userImageURL = null;
                if (!String.IsNullOrEmpty(userInfo?.KeyImageURL))
                {
                    userImageURL = HelperFunctions.GenerateUrlFromGenericImageUrl(userInfo.KeyImageURL);
                }

                using (Image image = await HelperFunctions.FetchImage(userImageURL))
                {
                    await DrawChatMessageImage(userInfo, image);
                }
                chatMessage = userInfo.ChatMessage;
            }
        }

        private async Task<bool> HandleChatMessageListNavigationKeys(ChatMessageListEventArgs e)
        {
            if (sequentialKey == 0)
            {
                await Connection.SetTitleAsync("Exit");
                return true;
            }
            
            if (e.ChatMessageKeys != null && sequentialKey == e.NumberOfKeys - 1 && e.ChatMessageKeys.Length > e.NumberOfKeys - 3) // Next key, and there is more than one page *overall*
            {
                if (e.ChatMessageKeys.Length + NUMBER_OF_SPECIAL_KEYS < pagedSequentialKey) // We are on last page
                {
                    await Connection.SetTitleAsync(null);
                }
                else
                {
                    await Connection.SetTitleAsync(">>");
                }
                numberOfElements = e.ChatMessageKeys.Length;
                numberOfKeys = e.NumberOfKeys;
                return true;
            }
            
            if (e.ChatMessageKeys != null && sequentialKey == e.NumberOfKeys - 2 && e.ChatMessageKeys.Length > e.NumberOfKeys - 3) // Prev key, and there is more than one page *overall*
            {
                if (sequentialKey == pagedSequentialKey) // We are on the first page
                {
                    await Connection.SetTitleAsync(null);
                }
                else
                {
                    await Connection.SetTitleAsync("<<");
                }
                numberOfElements = e.ChatMessageKeys.Length;
                numberOfKeys = e.NumberOfKeys;
                return true;
            }

            return false;
        }
        private async Task DrawChatMessageImage(ChatMessageKey keyInfo, Image background)
        {
            using (Bitmap bmp = Tools.GenerateGenericKeyImage(out Graphics graphics))
            {
                int height = bmp.Height;
                int width = bmp.Width;
                int textHeight = bmp.Height - 36;


                Font fontChannel = new Font("Verdana", 40, FontStyle.Bold, GraphicsUnit.Pixel);
                using (GraphicsPath gpath = new GraphicsPath())
                {
                    if (background != null)
                    {
                        // Draw background
                        graphics.DrawImage(background, 0, 0, width, textHeight);
                    }
                    else // If no image, put text in middle of key
                    {
                        textHeight = bmp.Height / 2;
                    }

                    // Set Streamer Name
                    gpath.AddString(keyInfo.KeyTitle,
                                        fontChannel.FontFamily,
                                        (int)FontStyle.Bold,
                                        graphics.DpiY * fontChannel.SizeInPoints / width,
                                        new Point(0, textHeight),
                                        new StringFormat());
                    graphics.DrawPath(Pens.Black, gpath);
                    graphics.FillPath(Brushes.White, gpath);

                    await Connection.SetImageAsync(bmp);
                    BackupCurrentImage(bmp);
                    fontChannel.Dispose();
                    graphics.Dispose();
                }
            }
        }

        private async Task DrawStreamerImage(TwitchChannelInfo streamerInfo, Image background)
        {
            using (Bitmap bmp = Tools.GenerateGenericKeyImage(out Graphics graphics))
            {
                int height = bmp.Height;
                int width = bmp.Width;

                Font fontChannel = new Font("Verdana", 44, FontStyle.Bold, GraphicsUnit.Pixel);
                Font fontViewers = new Font("Verdana", 44, FontStyle.Bold, GraphicsUnit.Pixel);
                Font fontIsStreaming = new Font("Webdings", 22, FontStyle.Regular, GraphicsUnit.Pixel);
                Font fontViewerCount = new Font("Webdings", 25, FontStyle.Regular, GraphicsUnit.Pixel);

                using (GraphicsPath gpath = new GraphicsPath())
                {
                    if (background != null)
                    {
                        // Draw background
                        graphics.DrawImage(background, 0, 0, width, height);
                    }

                    // Draw Viewer Count
                    graphics.DrawString("N", fontViewerCount, Brushes.White, new PointF(3, 8));
                    string viewers = $"{streamerInfo.Viewers}";
                    //graphics.DrawString(viewers, fontViewers, Brushes.White, new PointF(35, 3));
                    gpath.AddString(viewers,
                                        fontViewers.FontFamily,
                                        (int)FontStyle.Bold,
                                        graphics.DpiY * fontChannel.SizeInPoints / width,
                                        new Point(35, 7),
                                        new StringFormat());

                    // Draw Red Circle
                    graphics.DrawString("n", fontIsStreaming, Brushes.Red, new Point(3, 110));
                    int startWidth = 30;

                    // Set Streamer Name
                    gpath.AddString(streamerInfo.UserDisplayName,
                                        fontChannel.FontFamily,
                                        (int)FontStyle.Bold,
                                        graphics.DpiY * fontChannel.SizeInPoints / width,
                                        new Point(startWidth, 108),
                                        new StringFormat());
                    graphics.DrawPath(Pens.Black, gpath);
                    graphics.FillPath(Brushes.White, gpath);

                    await Connection.SetImageAsync(bmp);
                    BackupCurrentImage(bmp);
                    fontChannel.Dispose();
                    fontViewers.Dispose();
                    fontIsStreaming.Dispose();
                    fontViewerCount.Dispose();
                    graphics.Dispose();
                }
            }
        }

        private void BackupCurrentImage(Image img)
        {
            if (currentDrawnImage != null)
            {
                currentDrawnImage.Dispose();
                currentDrawnImage = null;
            }

            if (img != null)
            {
                currentDrawnImage = (Image) img.Clone();
            }
        }

        private async Task FlashImage(string pageMessage, Color flashColor)
        {
            await Connection.SetTitleAsync(null);

            if (flashColor == Color.Empty)
            {
                await Connection.SetImageAsync((string)null);
                return;
            }
            using (Bitmap img = Tools.GenerateGenericKeyImage(out Graphics graphics))
            {
                using (graphics)
                {
                    int height = img.Height;
                    int width = img.Width;

                    // Magic numbers after a bunch of trial and error :-/
                    int twoLetterFontSize = 80;
                    int oneLetterFontSize = 120;
                    int twoLetterTop = 15;
                    int oneLetterTop = 3;
                    int twoLetterBuffer = 65;

                    // Background
                    var bgBrush = new SolidBrush(flashColor);
                    graphics.FillRectangle(bgBrush, 0, 0, width, height);

                    if (String.IsNullOrEmpty(pageMessage) || stringMessageIndex < 0 || stringMessageIndex >= pageMessage?.Length)
                    {
                        await Connection.SetImageAsync(img);
                    }
                    else
                    {
                        var fgBrush = Brushes.White;
                        string letter = pageMessage[stringMessageIndex].ToString();

                        if (twoLettersPerKey) // 2 Letters per key
                        {
                            using (Font font = new Font("Verdana", twoLetterFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                if (pageMessage.Length > stringMessageIndex + 1)
                                {
                                    letter = pageMessage.Substring(stringMessageIndex, 2);
                                }

                                // Draw first letter
                                graphics.DrawString(letter[0].ToString(), font, fgBrush, new PointF(1, twoLetterTop));

                                if (letter.Length > 1)
                                {
                                    graphics.DrawString(letter[1].ToString(), font, fgBrush, new PointF(twoLetterBuffer, twoLetterTop));
                                }
                            }
                        }
                        else // 1 Letter per key
                        {
                            using (Font font = new Font("Verdana", oneLetterFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                SizeF stringSize = graphics.MeasureString(letter, font);
                                float stringPosX = 0;
                                float stringPosY = oneLetterTop;
                                if (stringSize.Width < img.Width)
                                {
                                    stringPosX = Math.Abs((img.Width - stringSize.Width)) / 2;
                                }
                                graphics.DrawString(letter, font, fgBrush, new PointF(stringPosX, stringPosY));
                            }
                        }
                        await Connection.SetImageAsync(img);
                    }
                }
            }
        }

        private void HandleLongKeyPress()
        {
            longKeyPressed = true;

            // Active Streamers 
            if (flashMode == FlashMode.ActiveStreamers)
            {
                if (sequentialKey == numberOfKeys - 1 && numberOfElements + 1 > pagedSequentialKey) // Next key is pressed
                {
                    // Move to next page
                    AlertManager.Instance.MoveToNextStreamersPage();
                }
                else if (!String.IsNullOrEmpty(channelName)) // Normal key
                {
                    if (liveStreamersLongPressAction == TwitchLiveStreamersLongPressAction.Raid)
                    {
                        TwitchChat.Instance.SendMessage(RAID_COMMAND + channelName);
                        ConfirmCurrentImage();
                    }
                    else
                    {
                        TwitchChat.Instance.SendMessage(HOST_COMMAND + channelName);
                        ConfirmCurrentImage();
                    }
                }
            }
        }

        private void ConfirmCurrentImage()
        {
            if (currentDrawnImage == null)
            {

                Logger.Instance.LogMessage(TracingLevel.WARN, $"{this.GetType()} ConfirmCurrentImage: Current drawn image is null!");
                return;
            }

            using (Graphics g = Graphics.FromImage(currentDrawnImage))
            {
                using (Image imgCheckBox = Tools.Base64StringToImage(Properties.Settings.Default.ImageGreenCheckbox))
                {
                    g.DrawImage(imgCheckBox, new Rectangle(new Point((currentDrawnImage.Width / 2) - (imgCheckBox.Width / 2), (currentDrawnImage.Height / 2) - (imgCheckBox.Height / 2)), new Size(imgCheckBox.Width, imgCheckBox.Height)));
                }
                Connection.SetImageAsync(currentDrawnImage).GetAwaiter().GetResult();
            }
        }
    }
}
