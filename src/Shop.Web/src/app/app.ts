import { Component, OnInit, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterLink, RouterOutlet } from '@angular/router';
import { Cart } from './store/cart';
import { ShopApi } from './store/shop-api';

@Component({
  imports: [RouterOutlet, RouterLink],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit {
  protected readonly shop = inject(ShopApi);
  protected readonly cart = inject(Cart);
  private readonly title = inject(Title);

  async ngOnInit(): Promise<void> {
    try {
      await this.shop.loadShop();
      this.title.setTitle(this.shop.shopName());
    } catch {
      // The header falls back to "Shop"; pages show their own errors.
    }
  }
}
