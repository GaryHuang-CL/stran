﻿/*
 * The contents of this file are subject to the Mozilla Public License
 * Version 1.1 (the "License"); you may not use this file except in
 * compliance with the License. You may obtain a copy of the License at
 * http://www.mozilla.org/MPL/
 * 
 * Software distributed under the License is distributed on an "AS IS"
 * basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
 * License for the specific language governing rights and limitations
 * under the License.
 * 
 * The Initial Developer of the Original Code is [MeteorRain <msg7086@gmail.com>].
 * Copyright (C) MeteorRain 2007, 2008. All Rights Reserved.
 * Contributor(s): [MeteorRain].
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace libTravian
{
	partial class Travian
	{
		public void FetchVillages()
		{
			Thread t = new Thread(new ThreadStart(doFetchVillages));
			t.Name = "FetchVillages";
			t.Start();
		}
		public void FetchVillageBuilding(int VillageID)
		{
			Thread t = new Thread(new ParameterizedThreadStart(doFetchVBuilding));
			t.Name = "FetchVillageBuilding";
			t.Start(VillageID);
		}
		public void FetchVillageUpgrade(int VillageID)
		{
			Thread t = new Thread(new ParameterizedThreadStart(doFetchVUpgrade));
			t.Name = "FetchVillageUpgrade";
			t.Start(VillageID);
		}
		public void FetchVillageDestroy(int VillageID)
		{
			Thread t = new Thread(new ParameterizedThreadStart(doFetchVDestroy));
			t.Name = "FetchVillageDestroy";
			t.Start(VillageID);
		}
		public void Cancel(int VillageID, int Key)
		{
			Thread t = new Thread(new ParameterizedThreadStart(doCancelWrapper));
			t.Name = "Cancel";
			t.Start(new CancelOption() { VillageID = VillageID, Key = Key });
		}
	}
}
