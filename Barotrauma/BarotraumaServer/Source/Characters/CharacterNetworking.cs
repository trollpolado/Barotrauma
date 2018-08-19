﻿using Barotrauma.Networking;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    partial class Character
    {
        public virtual void ServerRead(ClientNetObject type, NetBuffer msg, Client c)
        {
            if (GameMain.Server == null) return;

            switch (type)
            {
                case ClientNetObject.CHARACTER_INPUT:

                    if (c.Character != this)
                    {
#if DEBUG
                        DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                        return;
                    }

                    UInt16 networkUpdateID = msg.ReadUInt16();
                    byte inputCount = msg.ReadByte();

                    if (AllowInput) Enabled = true;

                    for (int i = 0; i < inputCount; i++)
                    {
                        InputNetFlags newInput = (InputNetFlags)msg.ReadRangedInteger(0, (int)InputNetFlags.MaxVal);
                        UInt16 newAim = 0;
                        UInt16 newInteract = 0;

                        if (newInput.HasFlag(InputNetFlags.Aim))
                        {
                            newAim = msg.ReadUInt16();
                        }
                        if (newInput.HasFlag(InputNetFlags.Select) ||
                            newInput.HasFlag(InputNetFlags.Use) ||
                            newInput.HasFlag(InputNetFlags.Health))
                        {
                            newInteract = msg.ReadUInt16();
                        }

                        //if (AllowInput)
                        //{
                        if (NetIdUtils.IdMoreRecent((ushort)(networkUpdateID - i), LastNetworkUpdateID) && (i < 60))
                        {
                            NetInputMem newMem = new NetInputMem();
                            newMem.states = newInput;
                            newMem.intAim = newAim;
                            newMem.interact = newInteract;

                            newMem.networkUpdateID = (ushort)(networkUpdateID - i);

                            memInput.Insert(i, newMem);
                        }
                        //}
                    }

                    if (NetIdUtils.IdMoreRecent(networkUpdateID, LastNetworkUpdateID))
                    {
                        LastNetworkUpdateID = networkUpdateID;
                    }
                    if (memInput.Count > 60)
                    {
                        //deleting inputs from the queue here means the server is way behind and data needs to be dropped
                        //we'll make the server drop down to 30 inputs for good measure
                        memInput.RemoveRange(30, memInput.Count - 30);
                    }
                    break;

                case ClientNetObject.ENTITY_STATE:
                    int eventType = msg.ReadRangedInteger(0, 3);
                    switch (eventType)
                    {
                        case 0:
                            Inventory.ServerRead(type, msg, c);
                            break;
                        case 1:
                            bool doingCPR = msg.ReadBoolean();
                            if (c.Character != this)
                            {
#if DEBUG
                                DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                                return;
                            }

                            AnimController.Anim = doingCPR ? AnimController.Animation.CPR : AnimController.Animation.None;
                            break;
                        case 2:
                            if (c.Character != this)
                            {
#if DEBUG
                                DebugConsole.Log("Received a character update message from a client who's not controlling the character");
#endif
                                return;
                            }

                            if (IsUnconscious)
                            {
                                var causeOfDeath = CharacterHealth.GetCauseOfDeath();
                                Kill(causeOfDeath.First, causeOfDeath.Second);
                            }
                            break;
                    }
                    break;
            }
            msg.ReadPadBits();
        }

        public virtual void ServerWrite(NetBuffer msg, Client c, object[] extraData = null)
        {
            if (GameMain.Server == null) return;

            if (extraData != null)
            {
                switch ((NetEntityEvent.Type)extraData[0])
                {
                    case NetEntityEvent.Type.InventoryState:
                        msg.WriteRangedInteger(0, 2, 0);
                        Inventory.ClientWrite(msg, extraData);
                        break;
                    case NetEntityEvent.Type.Control:
                        msg.WriteRangedInteger(0, 2, 1);
                        Client owner = ((Client)extraData[1]);
                        msg.Write(owner == null ? (byte)0 : owner.ID);
                        break;
                    case NetEntityEvent.Type.Status:
                        msg.WriteRangedInteger(0, 2, 2);
                        WriteStatus(msg);
                        break;
                    default:
                        DebugConsole.ThrowError("Invalid NetworkEvent type for entity " + ToString() + " (" + (NetEntityEvent.Type)extraData[0] + ")");
                        break;
                }
                msg.WritePadBits();
            }
            else
            {
                msg.Write(ID);

                NetBuffer tempBuffer = new NetBuffer();

                if (this == c.Character)
                {
                    tempBuffer.Write(true);
                    if (LastNetworkUpdateID < memInput.Count + 1)
                    {
                        tempBuffer.Write((UInt16)0);
                    }
                    else
                    {
                        tempBuffer.Write((UInt16)(LastNetworkUpdateID - memInput.Count - 1));
                    }
                }
                else
                {
                    tempBuffer.Write(false);

                    bool aiming = false;
                    bool use = false;
                    bool attack = false;

                    if (IsRemotePlayer)
                    {
                        aiming = dequeuedInput.HasFlag(InputNetFlags.Aim);
                        use = dequeuedInput.HasFlag(InputNetFlags.Use);

                        attack = dequeuedInput.HasFlag(InputNetFlags.Attack);
                    }
                    else if (keys != null)
                    {
                        aiming = keys[(int)InputType.Aim].GetHeldQueue;
                        use = keys[(int)InputType.Use].GetHeldQueue;

                        attack = keys[(int)InputType.Attack].GetHeldQueue;

                        networkUpdateSent = true;
                    }

                    tempBuffer.Write(aiming);
                    tempBuffer.Write(use);
                    if (AnimController is HumanoidAnimController)
                    {
                        tempBuffer.Write(((HumanoidAnimController)AnimController).Crouching);
                    }

                    bool hasAttackLimb = AnimController.Limbs.Any(l => l != null && l.attack != null);
                    tempBuffer.Write(hasAttackLimb);
                    if (hasAttackLimb) tempBuffer.Write(attack);

                    if (aiming)
                    {
                        Vector2 relativeCursorPos = cursorPosition - (ViewTarget == null ? AnimController.AimSourcePos : ViewTarget.Position);
                        tempBuffer.Write((UInt16)(65535.0 * Math.Atan2(relativeCursorPos.Y, relativeCursorPos.X) / (2.0 * Math.PI)));
                    }
                    tempBuffer.Write(IsRagdolled);

                    tempBuffer.Write(AnimController.TargetDir == Direction.Right);
                }

                if (SelectedCharacter != null || SelectedConstruction != null)
                {
                    tempBuffer.Write(true);
                    tempBuffer.Write(SelectedCharacter != null ? SelectedCharacter.ID : SelectedConstruction.ID);
                    if (SelectedCharacter != null)
                    {
                        tempBuffer.Write(AnimController.Anim == AnimController.Animation.CPR);
                    }
                }
                else
                {
                    tempBuffer.Write(false);
                }

                tempBuffer.Write(SimPosition.X);
                tempBuffer.Write(SimPosition.Y);
                tempBuffer.Write(AnimController.Collider.Rotation);

                WriteStatus(tempBuffer);

                tempBuffer.WritePadBits();

                msg.Write((byte)tempBuffer.LengthBytes);
                msg.Write(tempBuffer);
            }
        }

        public void WriteSpawnData(NetBuffer msg)
        {
            if (GameMain.Server == null) return;

            msg.Write(Info == null);
            msg.Write(ID);
            msg.Write(ConfigPath);
            msg.Write(seed);

            msg.Write(WorldPosition.X);
            msg.Write(WorldPosition.Y);

            msg.Write(Enabled);

            //character with no characterinfo (e.g. some monster)
            if (Info == null) return;

            msg.Write(info.ID);

            Client ownerClient = GameMain.Server.ConnectedClients.Find(c => c.Character == this);
            if (ownerClient != null)
            {
                msg.Write(true);
                msg.Write(ownerClient.ID);
            }
            else
            {
                msg.Write(false);
            }

            msg.Write(Info.Name);
            msg.Write(TeamID);

            msg.Write(this is AICharacter);
            msg.Write(Info.Gender == Gender.Female);
            msg.Write((byte)Info.HeadSpriteId);
            if (info.Job != null)
            {
                msg.Write(Info.Job.Prefab.Identifier);
                msg.Write((byte)info.Job.Skills.Count);
                foreach (Skill skill in info.Job.Skills)
                {
                    msg.Write(skill.Identifier);
                    msg.WriteRangedInteger(0, 100, (int)MathHelper.Clamp(skill.Level, 0, 100));
                }
            }
            else
            {
                msg.Write("");
            }
        }
    }
}