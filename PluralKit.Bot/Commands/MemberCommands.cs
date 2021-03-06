using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using NodaTime;

using PluralKit.Bot.CommandSystem;
using PluralKit.Core;

namespace PluralKit.Bot.Commands
{
    public class MemberCommands
    {
        private IDataStore _data;
        private EmbedService _embeds;

        private ProxyCacheService _proxyCache;

        public MemberCommands(IDataStore data, EmbedService embeds, ProxyCacheService proxyCache)
        {
            _data = data;
            _embeds = embeds;
            _proxyCache = proxyCache;
        }

        public async Task NewMember(Context ctx) {
            if (ctx.System == null) throw Errors.NoSystemError;
            var memberName = ctx.RemainderOrNull() ?? throw new PKSyntaxError("You must pass a member name.");
            
            // Hard name length cap
            if (memberName.Length > Limits.MaxMemberNameLength) throw Errors.MemberNameTooLongError(memberName.Length);

            // Warn if member name will be unproxyable (with/without tag)
            if (memberName.Length > ctx.System.MaxMemberNameLength) {
                var msg = await ctx.Reply($"{Emojis.Warn} Member name too long ({memberName.Length} > {ctx.System.MaxMemberNameLength} characters), this member will be unproxyable. Do you want to create it anyway? (You can change the name later, or set a member display name)");
                if (!await ctx.PromptYesNo(msg)) throw new PKError("Member creation cancelled.");
            }

            // Warn if there's already a member by this name
            var existingMember = await _data.GetMemberByName(ctx.System, memberName);
            if (existingMember != null) {
                var msg = await ctx.Reply($"{Emojis.Warn} You already have a member in your system with the name \"{existingMember.Name.SanitizeMentions()}\" (with ID `{existingMember.Hid}`). Do you want to create another member with the same name?");
                if (!await ctx.PromptYesNo(msg)) throw new PKError("Member creation cancelled.");
            }

            // Enforce per-system member limit
            var memberCount = await _data.GetSystemMemberCount(ctx.System);
            if (memberCount >= Limits.MaxMemberCount)
                throw Errors.MemberLimitReachedError;

            // Create the member
            var member = await _data.CreateMember(ctx.System, memberName);
            memberCount++;
            
            // Send confirmation and space hint
            await ctx.Reply($"{Emojis.Success} Member \"{memberName.SanitizeMentions()}\" (`{member.Hid}`) registered! See the user guide for commands for editing this member: https://pluralkit.me/guide#member-management");
            if (memberName.Contains(" "))
                await ctx.Reply($"{Emojis.Note} Note that this member's name contains spaces. You will need to surround it with \"double quotes\" when using commands referring to it, or just use the member's 5-character ID (which is `{member.Hid}`).");
            if (memberCount >= Limits.MaxMemberCount)
                await ctx.Reply($"{Emojis.Warn} You have reached the per-system member limit ({Limits.MaxMemberCount}). You will be unable to create additional members until existing members are deleted.");
            else if (memberCount >= Limits.MaxMembersWarnThreshold)
                await ctx.Reply($"{Emojis.Warn} You are approaching the per-system member limit ({memberCount} / {Limits.MaxMemberCount} members). Please review your member list for unused or duplicate members.");

            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task RenameMember(Context ctx, PKMember target) {
            // TODO: this method is pretty much a 1:1 copy/paste of the above creation method, find a way to clean?
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;
            
            var newName = ctx.RemainderOrNull();

            // Hard name length cap
            if (newName.Length > Limits.MaxMemberNameLength) throw Errors.MemberNameTooLongError(newName.Length);

            // Warn if member name will be unproxyable (with/without tag), only if member doesn't have a display name
            if (target.DisplayName == null && newName.Length > ctx.System.MaxMemberNameLength) {
                var msg = await ctx.Reply($"{Emojis.Warn} New member name too long ({newName.Length} > {ctx.System.MaxMemberNameLength} characters), this member will be unproxyable. Do you want to change it anyway? (You can set a member display name instead)");
                if (!await ctx.PromptYesNo(msg)) throw new PKError("Member renaming cancelled.");
            }

            // Warn if there's already a member by this name
            var existingMember = await _data.GetMemberByName(ctx.System, newName);
            if (existingMember != null) {
                var msg = await ctx.Reply($"{Emojis.Warn} You already have a member in your system with the name \"{existingMember.Name.SanitizeMentions()}\" (`{existingMember.Hid}`). Do you want to rename this member to that name too?");
                if (!await ctx.PromptYesNo(msg)) throw new PKError("Member renaming cancelled.");
            }

            // Rename the member
            target.Name = newName;
            await _data.SaveMember(target);

            await ctx.Reply($"{Emojis.Success} Member renamed.");
            if (newName.Contains(" ")) await ctx.Reply($"{Emojis.Note} Note that this member's name now contains spaces. You will need to surround it with \"double quotes\" when using commands referring to it.");
            if (target.DisplayName != null) await ctx.Reply($"{Emojis.Note} Note that this member has a display name set ({target.DisplayName.SanitizeMentions()}), and will be proxied using that name instead.");
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task MemberDescription(Context ctx, PKMember target) {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;

            var description = ctx.RemainderOrNull();
            if (description.IsLongerThan(Limits.MaxDescriptionLength)) throw Errors.DescriptionTooLongError(description.Length);

            target.Description = description;
            await _data.SaveMember(target);

            await ctx.Reply($"{Emojis.Success} Member description {(description == null ? "cleared" : "changed")}.");
        }
        
        public async Task MemberPronouns(Context ctx, PKMember target) {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;

            var pronouns = ctx.RemainderOrNull();
            if (pronouns.IsLongerThan(Limits.MaxPronounsLength)) throw Errors.MemberPronounsTooLongError(pronouns.Length);

            target.Pronouns = pronouns;
            await _data.SaveMember(target);

            await ctx.Reply($"{Emojis.Success} Member pronouns {(pronouns == null ? "cleared" : "changed")}.");
        }

        public async Task MemberColor(Context ctx, PKMember target)
        {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;

            var color = ctx.RemainderOrNull();
            if (color != null)
            {
                if (color.StartsWith("#")) color = color.Substring(1);
                if (!Regex.IsMatch(color, "^[0-9a-fA-F]{6}$")) throw Errors.InvalidColorError(color);
            }

            target.Color = color;
            await _data.SaveMember(target);

            await ctx.Reply($"{Emojis.Success} Member color {(color == null ? "cleared" : "changed")}.");
        }

        public async Task MemberBirthday(Context ctx, PKMember target)
        {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;
            
            LocalDate? date = null;
            var birthday = ctx.RemainderOrNull();
            if (birthday != null)
            {
                date = PluralKit.Utils.ParseDate(birthday, true);
                if (date == null) throw Errors.BirthdayParseError(birthday);
            }

            target.Birthday = date;
            await _data.SaveMember(target);
            
            await ctx.Reply($"{Emojis.Success} Member birthdate {(date == null ? "cleared" : $"changed to {target.BirthdayString}")}.");
        }

        public async Task MemberProxy(Context ctx, PKMember target)
        {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;
            
            // Handling the clear case in an if here to keep the body dedented
            var exampleProxy = ctx.RemainderOrNull();
            if (exampleProxy == null)
            {
                // Just reset and send OK message
                target.Prefix = null;
                target.Suffix = null;
                await _data.SaveMember(target);
                await ctx.Reply($"{Emojis.Success} Member proxy tags cleared.");
                return;
            }
            
            // Make sure there's one and only one instance of "text" in the example proxy given
            var prefixAndSuffix = exampleProxy.Split("text");
            if (prefixAndSuffix.Length < 2) throw Errors.ProxyMustHaveText;
            if (prefixAndSuffix.Length > 2) throw Errors.ProxyMultipleText;

            // If the prefix/suffix is empty, use "null" instead (for DB)
            target.Prefix = prefixAndSuffix[0].Length > 0 ? prefixAndSuffix[0] : null;
            target.Suffix = prefixAndSuffix[1].Length > 0 ? prefixAndSuffix[1] : null;
            await _data.SaveMember(target);
            await ctx.Reply($"{Emojis.Success} Member proxy tags changed to `{target.ProxyString.SanitizeMentions()}`. Try proxying now!");
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }

        public async Task MemberDelete(Context ctx, PKMember target)
        {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;
            
            await ctx.Reply($"{Emojis.Warn} Are you sure you want to delete \"{target.Name.SanitizeMentions()}\"? If so, reply to this message with the member's ID (`{target.Hid}`). __***This cannot be undone!***__");
            if (!await ctx.ConfirmWithReply(target.Hid)) throw Errors.MemberDeleteCancelled;
            await _data.DeleteMember(target);
            await ctx.Reply($"{Emojis.Success} Member deleted.");
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task MemberAvatar(Context ctx, PKMember target)
        {
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;
            
            if (await ctx.MatchUser() is IUser user)
            {
                if (user.AvatarId == null) throw Errors.UserHasNoAvatar;
                target.AvatarUrl = user.GetAvatarUrl(ImageFormat.Png, size: 256);
                
                await _data.SaveMember(target);
            
                var embed = new EmbedBuilder().WithImageUrl(target.AvatarUrl).Build();
                await ctx.Reply(
                    $"{Emojis.Success} Member avatar changed to {user.Username}'s avatar! {Emojis.Warn} Please note that if {user.Username} changes their avatar, the webhook's avatar will need to be re-set.", embed: embed);

            }
            else if (ctx.RemainderOrNull() is string url)
            {
                await Utils.VerifyAvatarOrThrow(url);
                target.AvatarUrl = url;
                await _data.SaveMember(target);

                var embed = new EmbedBuilder().WithImageUrl(url).Build();
                await ctx.Reply($"{Emojis.Success} Member avatar changed.", embed: embed);
            }
            else if (ctx.Message.Attachments.FirstOrDefault() is Attachment attachment)
            {
                await Utils.VerifyAvatarOrThrow(attachment.Url);
                target.AvatarUrl = attachment.Url;
                await _data.SaveMember(target);

                await ctx.Reply($"{Emojis.Success} Member avatar changed to attached image. Please note that if you delete the message containing the attachment, the avatar will stop working.");
            }
            else
            {
                target.AvatarUrl = null;
                await _data.SaveMember(target);
                await ctx.Reply($"{Emojis.Success} Member avatar cleared.");
            }
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }

        public async Task MemberDisplayName(Context ctx, PKMember target)
        {            
            if (ctx.System == null) throw Errors.NoSystemError;
            if (target.System != ctx.System.Id) throw Errors.NotOwnMemberError;

            var newDisplayName = ctx.RemainderOrNull();
            // Refuse if proxy name will be unproxyable (with/without tag)
            if (newDisplayName != null && newDisplayName.Length > ctx.System.MaxMemberNameLength)
                throw Errors.DisplayNameTooLong(newDisplayName, ctx.System.MaxMemberNameLength);
            
            target.DisplayName = newDisplayName;
            await _data.SaveMember(target);

            var successStr = $"{Emojis.Success} ";
            if (newDisplayName != null)
            {
                successStr +=
                    $"Member display name changed. This member will now be proxied using the name \"{newDisplayName.SanitizeMentions()}\".";
            }
            else
            {
                successStr += $"Member display name cleared. ";
                
                // If we're removing display name and the *real* name will be unproxyable, warn.
                if (target.Name.Length > ctx.System.MaxMemberNameLength)
                    successStr +=
                        $" {Emojis.Warn} This member's actual name is too long ({target.Name.Length} > {ctx.System.MaxMemberNameLength} characters), and thus cannot be proxied.";
                else
                    successStr += $"This member will now be proxied using their member name \"{target.Name.SanitizeMentions()}\".";
            }
            await ctx.Reply(successStr);
            
            await _proxyCache.InvalidateResultsForSystem(ctx.System);
        }
        
        public async Task ViewMember(Context ctx, PKMember target)
        {
            var system = await _data.GetSystemById(target.System);
            await ctx.Reply(embed: await _embeds.CreateMemberEmbed(system, target));
        }
    }
}